using System.Diagnostics;
using System.Text;
using Brobot.Connection;

namespace Brobot.Sender.Gba;

/// <summary>
/// Runs a libretro core on a dedicated thread, paced at the GBA's own
/// ~59.73 fps, and streams the video to Peemo over STREAM/FRAME (see
/// PROTOCOL.md) — the first end-to-end hookup of Gba/LibretroCore.cs (Phase
/// 1, proven separately) to Phase 0's STREAM/FRAME protocol. Still a
/// throwaway test path, not the real Mini Games feature (see
/// specs/sender-gba.md's implementation order) — no audio yet (the core's
/// audio callback exists but discards samples, see LibretroCore) and no
/// input (every button reads as not-pressed).
///
/// Emulation and sending are deliberately decoupled: this thread always
/// steps the core at real GBA speed regardless of the link, and only
/// *sends* a frame when the previous one's FRAMEOK has already landed —
/// exactly the "one frame in flight, older ones are simply skipped" shape
/// specs/sender-gba.md's Frame pipeline describes. Both the downscaled
/// "current" frame and the diff against the last *sent* one are read/written
/// only from this thread (OnFrameReady and TrySendFrame both run inside the
/// same RunLoop iteration, back to back) — the one piece of state another
/// thread touches is the ack flag, via OnFrameAck.
/// </summary>
public sealed class GbaSession : IDisposable
{
    private const int GbaWidth = 240;
    private const int GbaHeight = 160;
    private const int OutWidth = 160;
    private const int OutHeight = 107;
    // 128 - 107 = 21 spare rows — 10 above, 11 below (see PROTOCOL.md's
    // STREAM/FRAME section and specs/sender-gba.md's frame pipeline).
    private const int YOffset = 10;
    private const int RowPayloadBytes = OutWidth * 2;
    private const int RowRecordBytes = RowPayloadBytes + 1;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 59.7275);

    private readonly BrobotConnection _connection;
    private readonly string _corePath;
    private readonly string _workingDirectory;
    private readonly string _romPath;

    private readonly ushort[,] _currentFrame = new ushort[OutHeight, OutWidth];
    private readonly ushort[,] _lastSentFrame = new ushort[OutHeight, OutWidth];
    private bool _hasSentFrame;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _awaitingAck;
    private volatile int _seq;

    public event Action<string>? StatusChanged;

    public GbaSession(BrobotConnection connection, string corePath, string workingDirectory, string romPath)
    {
        _connection = connection;
        _corePath = corePath;
        _workingDirectory = workingDirectory;
        _romPath = romPath;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _hasSentFrame = false;
        _awaitingAck = false;
        _seq = 0;

        _connection.SendCommand("STREAM START");

        _thread = new Thread(RunLoop) { IsBackground = true, Name = "GbaSession" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _thread?.Join(2000);
        _thread = null;

        _connection.SendCommand("STREAM STOP");
    }

    /// <summary>Called from BrobotConnection's read thread (via MainWindow's OnFrameReceived) when a FRAMEOK line arrives.</summary>
    public void OnFrameAck(ushort seq)
    {
        if (_awaitingAck && seq == (ushort)_seq)
        {
            _awaitingAck = false;
        }
    }

    private void RunLoop()
    {
        LibretroCore? core = null;
        try
        {
            core = new LibretroCore(_corePath, _workingDirectory);
            core.FrameReady += OnFrameReady;

            if (!core.LoadGame(_romPath))
            {
                StatusChanged?.Invoke("Falha ao carregar a ROM.");
                return;
            }

            var clock = Stopwatch.StartNew();
            TimeSpan nextFrameAt = clock.Elapsed;
            int emulatedFrames = 0;
            int sentFrames = 0;
            var statsClock = Stopwatch.StartNew();

            while (_running)
            {
                core.Run();
                emulatedFrames++;

                if (TrySendFrame())
                {
                    sentFrames++;
                }

                nextFrameAt += FrameInterval;
                TimeSpan remaining = nextFrameAt - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    Thread.Sleep(remaining);
                }
                else
                {
                    // Fell behind (this iteration's work took longer than
                    // one frame's budget) — resync to now instead of trying
                    // to burst through a backlog of "due" frames.
                    nextFrameAt = clock.Elapsed;
                }

                if (statsClock.Elapsed.TotalSeconds >= 1)
                {
                    StatusChanged?.Invoke($"Rodando — {emulatedFrames} frames emulados/s, {sentFrames} enviados/s");
                    emulatedFrames = 0;
                    sentFrames = 0;
                    statsClock.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Erro: {ex.Message}");
        }
        finally
        {
            core?.Dispose();
        }
    }

    /// <summary>
    /// Nearest-neighbour downscale straight into RGB565. specs/sender-gba.md
    /// calls area averaging the "try first" option and nearest-neighbour
    /// the fallback — starting with the simpler one and judging quality on
    /// the device before bothering with averaging.
    /// </summary>
    private void OnFrameReady(LibretroCore.PixelFormat format, int width, int height, byte[] pixels)
    {
        int bytesPerPixel = format == LibretroCore.PixelFormat.Xrgb8888 ? 4 : 2;
        int rowBytes = width * bytesPerPixel;

        for (int oy = 0; oy < OutHeight; oy++)
        {
            int sy = oy * height / OutHeight;
            int rowOffset = sy * rowBytes;
            for (int ox = 0; ox < OutWidth; ox++)
            {
                int sx = ox * width / OutWidth;
                _currentFrame[oy, ox] = ReadRgb565(format, pixels, rowOffset + sx * bytesPerPixel);
            }
        }
    }

    private static ushort ReadRgb565(LibretroCore.PixelFormat format, byte[] pixels, int offset)
    {
        switch (format)
        {
            case LibretroCore.PixelFormat.Rgb565:
                return (ushort)(pixels[offset] | (pixels[offset + 1] << 8));

            case LibretroCore.PixelFormat.Xrgb8888:
            {
                // Memory order is B,G,R,X (little-endian 0xXXRRGGBB) — see
                // LibretroCore's own pixel-format comment.
                byte b = pixels[offset];
                byte g = pixels[offset + 1];
                byte r = pixels[offset + 2];
                return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
            }

            default: // Rgb1555 (0RGB1555)
            {
                ushort px = (ushort)(pixels[offset] | (pixels[offset + 1] << 8));
                int r5 = (px >> 10) & 0x1F;
                int g5 = (px >> 5) & 0x1F;
                int b5 = px & 0x1F;
                int g6 = (g5 << 1) | (g5 >> 4); // 5->6 bit expand, same trick as the firmware's fringing math
                return (ushort)((r5 << 11) | (g6 << 5) | b5);
            }
        }
    }

    /// <summary>Returns true if a frame was actually sent (link was ready and something had changed).</summary>
    private bool TrySendFrame()
    {
        if (_awaitingAck || !_connection.IsConnected)
        {
            return false;
        }

        var touchedRows = new List<int>();
        for (int y = 0; y < OutHeight; y++)
        {
            bool changed = !_hasSentFrame;
            if (!changed)
            {
                for (int x = 0; x < OutWidth; x++)
                {
                    if (_currentFrame[y, x] != _lastSentFrame[y, x])
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (changed)
            {
                touchedRows.Add(y);
            }
        }

        if (touchedRows.Count == 0)
        {
            return false;
        }

        byte[] payload = new byte[touchedRows.Count * RowRecordBytes];
        int offset = 0;
        foreach (int row in touchedRows)
        {
            payload[offset++] = (byte)(row + YOffset);
            for (int x = 0; x < OutWidth; x++)
            {
                ushort px = _currentFrame[row, x];
                payload[offset++] = (byte)(px >> 8);
                payload[offset++] = (byte)(px & 0xFF);
                _lastSentFrame[row, x] = px;
            }
        }

        _hasSentFrame = true;
        int seq = ++_seq;
        _awaitingAck = true;

        byte[] header = Encoding.ASCII.GetBytes($"FRAME {(ushort)seq} {payload.Length}\n");
        byte[] framed = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, framed, 0, header.Length);
        Buffer.BlockCopy(payload, 0, framed, header.Length, payload.Length);
        _connection.SendRawBytes(framed);
        return true;
    }

    public void Dispose() => Stop();
}
