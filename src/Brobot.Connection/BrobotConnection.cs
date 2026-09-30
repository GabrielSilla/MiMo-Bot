using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;

namespace Brobot.Connection;

/// <summary>
/// Talks Brobot Core's line protocol (see PROTOCOL.md) over either a real
/// serial (COM) port — a physical Arduino/ESP32 — or, for local dev without
/// hardware, a loopback TCP connection to a native (non-Arduino) BrobotCore
/// build that listens the same way a real device's COM port would (see
/// BrobotCore/native/README.md). Core is always "the device": whichever
/// transport, this class connects TO it, never the other way around, so
/// several apps (Brobot Virtual Display, Brobot.Sender) can each hold their
/// own independent connection to the same running Core at once.
///
/// Handles the two things every consumer needs regardless of what it does
/// with the lines: batching incoming lines into frames (a "PRESENT" line
/// ends a frame) and dispatching each frame in one synchronous
/// Dispatcher.Invoke call, plus sending outgoing command lines with a
/// BOM-safe encoding over TCP. What a frame's lines mean is up to the
/// caller (<see cref="FrameReceived"/>) — this class has no opinion about
/// drawing or personality.
/// </summary>
public sealed class BrobotConnection : IDisposable
{
    private readonly Dispatcher _dispatcher;

    private SerialPort? _port;
    // Tracked separately from _port.IsOpen: reading that property from the UI
    // thread while the background IO thread has a pending ReadLine() on the
    // same SerialPort hung indefinitely against the ESP32-C3 SuperMini's
    // native USB-CDC port (a real UART bridge chip like the Uno's never
    // showed this). This was a real bug, fixed once — IsConnected/SendCommand
    // must never touch _port's properties from another thread again.
    private volatile bool _serialOpen;
    // True from ConnectSerial until the open (and, if asked for, the PING
    // verification) either succeeds or gives up — lets a caller tell "still
    // trying" from "tried and failed" without an event.
    private volatile bool _serialConnecting;
    // Serial writes come from the UI thread (commands) and the keepalive
    // timer alike, and SerialPort.Write isn't safe to interleave.
    private readonly object _serialWriteLock = new();

    private TcpClient? _tcpClient;
    private StreamWriter? _tcpWriter;
    // Same underlying stream _tcpWriter wraps — kept separately so
    // SendRawBytes can write bytes straight through without StreamWriter's
    // text encoding mangling them (see SendRawBytes's own comment).
    private NetworkStream? _tcpStream;
    private string? _tcpHost;
    private int _tcpPort;
    private volatile bool _tcpConnected;
    // Guards every write to _tcpStream/_tcpWriter — SendCommand (text) and
    // SendRawBytes (binary, e.g. STREAM/FRAME payloads — see PROTOCOL.md)
    // can be called from different threads, and TCP is a single ordered
    // byte stream: without this, a raw frame write and a command line write
    // could interleave mid-write and corrupt both.
    private readonly object _tcpWriteLock = new();

    private Thread? _ioThread;
    private volatile bool _running;

    public BrobotConnection(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread once per frame: every line received up to and including "PRESENT".</summary>
    public event Action<IReadOnlyList<string>>? FrameReceived;

    /// <summary>
    /// Raised on the IO thread for every line the moment it arrives, before
    /// any frame batching or UI hop. For latency-sensitive replies only (a
    /// FRAMEOK gates the next STREAM frame; going through the UI thread first
    /// added its scheduling delay to every frame). Handlers must be quick and
    /// must not touch UI.
    /// </summary>
    public event Action<string>? LineReceived;

    public bool IsConnected => _serialOpen || _tcpConnected;

    /// <summary>True while connected over Serial specifically (with verification asked for: once the board has answered PING).</summary>
    public bool IsSerialConnected => _serialOpen;

    /// <summary>
    /// Why the last ConnectSerial attempt ended without a connection (in words a
    /// person can act on), or null while it hasn't failed. Cleared by the next
    /// ConnectSerial.
    /// </summary>
    public string? LastSerialFailure => _lastSerialFailure;
    private volatile string? _lastSerialFailure;

    /// <summary>True while ConnectSerial is still opening/verifying the port.</summary>
    public bool IsConnectingSerial => _serialConnecting;

    /// <summary>True while ConnectTcp is retrying but hasn't reached Core yet.</summary>
    public bool IsConnectingTcp => _tcpHost != null && !_tcpConnected;

    public static string[] GetAvailablePortNames() => SerialPort.GetPortNames();

    /// <summary>
    /// Opens the port and starts reading, both on a background thread — never
    /// on the caller's. .NET's SerialPort.Open() can hang for many seconds
    /// (sometimes indefinitely) against an ESP32's native USB-CDC serial port
    /// specifically (unlike a "real" UART bridge chip like the Uno's), and
    /// this used to call it directly from the WPF button handler, freezing
    /// the whole app on connect. This was a real bug, fixed once.
    /// </summary>
    /// <param name="verifyPeemo">
    /// Sends PING right after opening and only counts as connected once a
    /// "PEEMO ..." line comes back (see PROTOCOL.md); anything else on that
    /// port — another Espressif board, a Peemo firmware that doesn't listen
    /// on USB — is closed again within a few seconds and leaves this
    /// disconnected. Off by default: the Simulator's Uno/ESP32 never answers.
    /// </param>
    public void ConnectSerial(string portName, int baudRate = 115200, bool verifyPeemo = false)
    {
        Disconnect();

        _running = true;
        _serialConnecting = true;
        _lastSerialFailure = null;
        _ioThread = new Thread(() => SerialOpenAndReadLoop(portName, baudRate, verifyPeemo))
            { IsBackground = true, Name = "BrobotConnection-SerialRead" };
        _ioThread.Start();
    }

    /// <summary>
    /// Connects to Core's TCP listener (BrobotCore/native), retrying every
    /// 500ms until it succeeds or Disconnect() is called — Core and this
    /// app can be started in either order, and if Core is restarted
    /// mid-session this reconnects on its own without the caller doing
    /// anything.
    /// </summary>
    public void ConnectTcp(string host, int port)
    {
        Disconnect();

        _tcpHost = host;
        _tcpPort = port;

        _running = true;
        _ioThread = new Thread(TcpConnectAndReadLoop) { IsBackground = true, Name = "BrobotConnection-TcpConnect" };
        _ioThread.Start();
    }

    /// <summary>Sends a raw control command line (e.g. "FACE HAPPY", "MSG Ola") to Core.</summary>
    public void SendCommand(string command)
    {
        if (_serialOpen && _port is { } port)
        {
            lock (_serialWriteLock)
            {
                try
                {
                    port.Write(command + "\n");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
                {
                    // Cable pulled mid-write; the read loop notices and cleans up.
                }
            }
            return;
        }

        lock (_tcpWriteLock)
        {
            try
            {
                _tcpWriter?.Write(command + "\n");
                _tcpWriter?.Flush();
            }
            catch (IOException)
            {
                // Core disconnected; the read loop will notice and clean up.
            }
        }
    }

    /// <summary>
    /// Writes raw bytes straight to the TCP stream — for the one place the
    /// protocol isn't a text line, a binary FRAME payload (see PROTOCOL.md's
    /// STREAM/FRAME section): a "FRAME &lt;seq&gt; &lt;bytes&gt;" header
    /// followed immediately by exactly that many bytes of pixel data. Must
    /// bypass _tcpWriter's StreamWriter: its UTF8 encoding would transcode
    /// any raw byte outside plain ASCII instead of passing it through
    /// unchanged, silently corrupting the payload. TCP-only, same as the
    /// rest of this class's write path. Goes to the serial port when that is
    /// the live link (Write(byte[]) has no text encoding in the way either).
    /// A no-op while not connected at all.
    /// </summary>
    public void SendRawBytes(byte[] data)
    {
        if (_serialOpen && _port is { } port)
        {
            lock (_serialWriteLock)
            {
                try
                {
                    port.Write(data, 0, data.Length);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
                {
                    // Same as SendCommand: the read loop cleans up.
                }
            }
            return;
        }

        lock (_tcpWriteLock)
        {
            if (_tcpStream is not { } stream)
            {
                return;
            }

            try
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            catch (IOException)
            {
                // Core disconnected; the read loop will notice and clean up.
            }
        }
    }

    public void Disconnect()
    {
        _running = false;
        _tcpHost = null;

        _tcpClient?.Close(); // unblocks a pending Connect/ReadLine in the IO thread

        _ioThread?.Join(1000);
        _ioThread = null;
        _serialOpen = false;

        if (_port != null)
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch (IOException)
            {
            }

            _port.Dispose();
            _port = null;
        }

        _tcpWriter?.Dispose();
        _tcpWriter = null;
        _tcpStream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
        _tcpConnected = false;
    }

    public void Dispose() => Disconnect();

    private void SerialOpenAndReadLoop(string portName, int baudRate, bool verifyPeemo)
    {
        try
        {
            SerialOpenVerifyAndRead(portName, baudRate, verifyPeemo);
        }
        finally
        {
            _serialConnecting = false;
        }
    }

    private void SerialOpenVerifyAndRead(string portName, int baudRate, bool verifyPeemo)
    {
        // UTF-8 both ways (SerialPort defaults to ASCII, which would turn
        // every "ã"/"ç" in a MSG into '?' — TCP already sends UTF-8). DTR on,
        // like every terminal program: the ESP32-C3's USB-CDC may hold back
        // output until the host says it is listening. WriteTimeout so a
        // yanked cable can't wedge the UI thread inside Write().
        var port = new SerialPort(portName, baudRate)
        {
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 1000,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            DtrEnable = true,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            // Bad/missing port name, already in use, or the device dropped off
            // mid-open — same "leave disconnected, IsConnected stays false"
            // outcome as any other failed connect.
            _lastSerialFailure = ex is UnauthorizedAccessException
                ? $"{portName} está em uso por outro programa"
                : $"não deu para abrir a {portName} ({ex.Message})";
            port.Dispose();
            return;
        }

        if (!_running)
        {
            // Disconnect() was called while Open() was still working.
            port.Dispose();
            return;
        }

        _port = port;
        if (verifyPeemo && !VerifyPeemo(port))
        {
            // Not answering PING: same outcome as a failed open. The port has
            // to be released here — left open, the COM name would stay locked
            // and every later attempt would fail with "access denied".
            _port = null;
            if (_running)
            {
                _lastSerialFailure = $"a {portName} abriu, mas o Peemo não respondeu ao PING (outra placa, ou firmware sem USB?)";
            }
            try
            {
                port.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
            }
            return;
        }

        _serialOpen = true;
        try
        {
            SerialReadLoop(port);
        }
        finally
        {
            _serialOpen = false;
        }
    }

    private static readonly TimeSpan PeemoVerifyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// PING until "PEEMO ..." comes back or PeemoVerifyTimeout passes. The
    /// PING is repeated on every read timeout (~500ms) because the very first
    /// one can land while the board's USB is still enumerating and be dropped.
    /// Anything else the port says (boot logs, ESP-IDF log lines) is skipped.
    /// </summary>
    private bool VerifyPeemo(SerialPort port)
    {
        DateTime deadline = DateTime.UtcNow + PeemoVerifyTimeout;
        bool needPing = true;
        while (_running && DateTime.UtcNow < deadline)
        {
            try
            {
                if (needPing)
                {
                    port.Write("PING\n");
                    needPing = false;
                }

                string line = port.ReadLine();
                if (line.StartsWith("PEEMO", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (TimeoutException)
            {
                needPing = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private void SerialReadLoop(SerialPort port)
    {
        var pendingLines = new List<string>();

        while (_running && port.IsOpen)
        {
            string line;
            try
            {
                line = port.ReadLine();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Port was closed, or the USB device dropped/reset, while a read was in
                // flight (SerialPort's internal async read surfaces that as OperationCanceledException).
                return;
            }

            AccumulateAndFlushOnFrameEnd(pendingLines, line);
        }
    }

    private void TcpConnectAndReadLoop()
    {
        var pendingLines = new List<string>();

        while (_running)
        {
            // Nagle (on by default) batches up small writes waiting to see
            // if more data is coming, which is invisible for this class's
            // normal fire-and-forget commands (FACE/MSG/PONG KEY, never
            // waited on) but stalls a tight request-response exchange like
            // STREAM/FRAME's one-frame-in-flight ack (see PROTOCOL.md) by
            // tens to hundreds of ms per round trip — PeemoDiscovery.cs
            // already disables it for the same reason, on its own
            // short-lived probe connections.
            var client = new TcpClient { NoDelay = true };
            try
            {
                // ConnectAsync + a bounded Wait so Disconnect() (which flips
                // _running) is noticed promptly instead of blocking on the
                // OS's much longer default TCP connect timeout.
                Task connectTask = client.ConnectAsync(_tcpHost!, _tcpPort);
                connectTask.Wait(500);
            }
            catch (AggregateException)
            {
                // Connection refused (Core not listening yet) — retry below.
            }

            if (!_running)
            {
                client.Dispose();
                return;
            }

            if (!client.Connected)
            {
                client.Dispose();
                Thread.Sleep(200);
                continue;
            }

            _tcpClient = client;
            NetworkStream stream = client.GetStream();
            _tcpStream = stream;
            // StreamWriter's default UTF8 encoding emits a byte-order-mark
            // preamble on its first write, which would silently corrupt the
            // first protocol line ever sent (its command name no longer
            // matches "FACE"/"MSG", so Protocol::dispatch just ignores it).
            _tcpWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
            _tcpConnected = true;

            using (var reader = new StreamReader(stream))
            {
                while (_running)
                {
                    string? line;
                    try
                    {
                        line = reader.ReadLine();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        break;
                    }

                    if (line == null)
                    {
                        break; // Core closed the connection
                    }

                    AccumulateAndFlushOnFrameEnd(pendingLines, line);
                }
            }

            _tcpWriter.Dispose();
            _tcpWriter = null;
            _tcpStream = null;
            _tcpClient.Dispose();
            _tcpClient = null;
            _tcpConnected = false;

            if (_running)
            {
                Thread.Sleep(200); // brief pause before reconnecting
            }
        }
    }

    /// <summary>
    /// Buffers lines until a "PRESENT" completes a frame, then dispatches
    /// the whole frame in one <see cref="Dispatcher.Invoke"/> call instead
    /// of one per line — a frame is ~20-30 lines (two eyes' rounded-rect
    /// corner cuts, plus more with a message showing), and marshaling to
    /// the UI thread that many times per frame was itself a bottleneck once
    /// Core's frame rate was raised for TCP dev use.
    ///
    /// Synchronous on purpose: BeginInvoke would queue frames without ever
    /// waiting, so if the UI thread ever falls even slightly behind (window
    /// drag, GC pause, anything), the backlog has no way to drain and a
    /// fresh MSG meant to interrupt what's showing would end up stuck
    /// behind stale frames. Invoke blocks this thread until the UI catches
    /// up, which naturally throttles reading to real rendering speed
    /// instead of piling up.
    /// </summary>
    private void AccumulateAndFlushOnFrameEnd(List<string> pendingLines, string line)
    {
        // A serial port also carries the board's own log lines, which never
        // end in PRESENT; without a cap they'd pile up for the whole session.
        if (pendingLines.Count > 2000)
        {
            pendingLines.Clear();
        }
        LineReceived?.Invoke(line);
        pendingLines.Add(line);

        if (line.TrimEnd('\r', '\n') != "PRESENT")
        {
            return;
        }

        string[] frame = pendingLines.ToArray();
        pendingLines.Clear();
        _dispatcher.Invoke(() => FrameReceived?.Invoke(frame));
    }
}
