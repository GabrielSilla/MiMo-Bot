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

    private TcpClient? _tcpClient;
    private StreamWriter? _tcpWriter;
    private string? _tcpHost;
    private int _tcpPort;
    private volatile bool _tcpConnected;

    private Thread? _ioThread;
    private volatile bool _running;

    public BrobotConnection(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Raised on the UI thread once per frame: every line received up to and including "PRESENT".</summary>
    public event Action<IReadOnlyList<string>>? FrameReceived;

    public bool IsConnected => _serialOpen || _tcpConnected;

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
    public void ConnectSerial(string portName, int baudRate = 115200)
    {
        Disconnect();

        _running = true;
        _ioThread = new Thread(() => SerialOpenAndReadLoop(portName, baudRate))
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
            port.Write(command + "\n");
            return;
        }

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
        _tcpClient?.Dispose();
        _tcpClient = null;
        _tcpConnected = false;
    }

    public void Dispose() => Disconnect();

    private void SerialOpenAndReadLoop(string portName, int baudRate)
    {
        var port = new SerialPort(portName, baudRate) { NewLine = "\n", ReadTimeout = 500 };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException)
        {
            // Bad/missing port name, already in use, or the device dropped off
            // mid-open — same "leave disconnected, IsConnected stays false"
            // outcome as any other failed connect.
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
            var client = new TcpClient();
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
