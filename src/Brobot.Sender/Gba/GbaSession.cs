using System.Diagnostics;
using System.IO;
using System.Text;
using Brobot.Connection;

namespace Brobot.Sender.Gba;

/// <summary>
/// Runs a libretro core on a dedicated thread, paced at the GBA's own
/// ~59.73 fps, and streams the video to Peemo over STREAM/FRAME (see
/// PROTOCOL.md) — driving both the Modo teste "Transmitir" harness and the
/// real Mini Games GBA card (see MainWindow.xaml.cs). No audio yet (the
/// core's audio callback exists but discards samples, see LibretroCore);
/// input is wired (see SetButtonState) but this class itself doesn't read a
/// keyboard — that's MainWindow's GlobalKeyboardHook forwarding into it.
///
/// Emulation and sending are deliberately decoupled, on two threads: the
/// RunLoop thread steps the core at real GBA speed regardless of the link and
/// only *publishes* each downscaled frame (a cheap copy); a separate sender
/// thread takes the latest published frame whenever the previous one's
/// FRAMEOK has landed, diffs, encodes and writes it — "one frame in flight,
/// older ones are simply skipped", as specs/sender-gba.md's Frame pipeline
/// describes. Sending used to run inside the RunLoop iteration itself, and a
/// busy scene (the whole screen scrolling: ~35KB to encode and write to the
/// port) made that iteration longer than a 16.7ms frame, so the *game* ran in
/// slow motion, not just the picture on Peemo. The state the sender thread
/// owns alone: the copy it is sending and the last frame it sent.
/// </summary>
public sealed class GbaSession : IDisposable
{
    // Peemo's whole screen. Every console's picture is scaled to fit inside it
    // keeping its aspect ratio and centered on black (see OnFrameReady) — GBA's
    // 240x160 comes out 160x107 as it always did, SNES's 256x224 comes out
    // 146x128 with black bars left and right.
    private const int OutWidth = 160;
    private const int OutHeight = 128;
    // [row][encoding][length, 16-bit BE] — see Config.h's STREAM_ROW_*
    // comment on the firmware side.
    private const int RowHeaderBytes = 4;
    private const int RowMaxPayloadBytes = OutWidth * 2; // raw encoding's fixed size, RLE's ceiling
    private const byte RowEncodingRaw = 0;
    private const byte RowEncodingRle = 1;
    // Only the fallback: the core reports the console's real rate (see LibretroCore.Fps).
    private const double DefaultFps = 59.7275;

    private readonly BrobotConnection _connection;
    private readonly string _corePath;
    private readonly string _workingDirectory;
    private readonly string _romPath;

    // Written only by the RunLoop thread (OnFrameReady).
    private int _imageWidth;
    private int _imageHeight;
    private readonly ushort[,] _currentFrame = new ushort[OutHeight, OutWidth];
    // Hand-off between the two threads: RunLoop copies _currentFrame here
    // under _frameLock after every core.Run(); the sender takes the latest.
    private readonly ushort[,] _publishedFrame = new ushort[OutHeight, OutWidth];
    private readonly object _frameLock = new();
    private readonly ManualResetEventSlim _frameAvailable = new(false);
    // Sender-thread only.
    private readonly ushort[,] _sendFrame = new ushort[OutHeight, OutWidth];
    private readonly ushort[,] _lastSentFrame = new ushort[OutHeight, OutWidth];
    private bool _hasSentFrame;

    private Thread? _thread;
    private Thread? _senderThread;
    private volatile bool _running;
    // Frames sent but not yet acked. More than one in flight (MaxInFlight) so
    // Peemo never sits idle waiting for the PC's next frame while it is busy
    // drawing the previous one: the next frame is already in the USB/TCP
    // buffer when it finishes. Frames are still processed strictly in order
    // (one ordered byte stream), so the row diff against _lastSentFrame stays
    // valid. _slots counts free slots; _inFlight holds (seq, send time) of
    // each outstanding frame for RTT and for matching acks.
    private const int MaxInFlight = 2;
    private readonly SemaphoreSlim _slots = new(MaxInFlight, MaxInFlight);
    // ...but never more bytes than fit in Core's USB receive ring (see
    // USB_RX_BUFFER_BYTES in Config.h, 64KB): a busy scene makes each frame
    // ~35KB, two of those overflow it, Core drops bytes, abandons the frame and
    // never acks, and every such loss costs a full AckTimeout of frozen picture.
    // A heavy frame therefore goes out alone; light ones still overlap.
    private const int InFlightByteBudget = 60000;
    private readonly List<(int Seq, long Ticks, int Bytes)> _inFlight = new();
    private readonly object _inFlightLock = new();
    private volatile int _seq;
    private int _sentFrames;
    private int _maxFrameBytes;
    private int _rowsSent;
    // Set once RunLoop creates it, read from whatever thread is driving
    // input (see SetButtonState) — a UI-thread keyboard hook callback, not
    // this class's own thread, so this needs to be volatile for visibility.
    private volatile LibretroCore? _core;

    // Round-trip instrumentation (see StatusChanged's periodic summary):
    // written from two different threads (send happens on RunLoop's own
    // thread, the ack that completes it arrives via OnFrameAck on
    // BrobotConnection's read thread), so these are Interlocked rather than
    // plain fields. _frameSentAtTicks only ever needs to be "the most
    // recent send" — one-frame-in-flight means there's never more than one
    // outstanding send to time at once.
    private long _rttTotalMs;
    private int _rttSampleCount;

    // How long the sender waits for a FRAMEOK before giving up on it — a dropped ack
    // (a WiFi hiccup, or the firmware's own LINE_STALE_TIMEOUT_MS abandoning
    // a frame mid-transfer, see Protocol.cpp) must not wedge one-frame-in-
    // flight forever: with nothing to time it out, a single lost ack would
    // leave the ack wait stuck for the rest of the session, silently
    // freezing Peemo's screen on whatever was last drawn.
    // Two routes with different guarantees. USB is a dedicated, fast, steady
    // link: a missing ack really is a lost frame, so give up quickly (and its
    // receive ring is small enough that the byte budget below matters). WiFi is
    // allowed to hiccup — retransmits, a busy router — so it waits longer
    // before writing a frame off, and TCP's own flow control already keeps it
    // from overrunning Core, so no byte budget applies.
    private static readonly TimeSpan UsbAckTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WifiAckTimeout = TimeSpan.FromMilliseconds(600);
    private TimeSpan AckTimeout => _connection.IsSerialConnected ? UsbAckTimeout : WifiAckTimeout;
    private int _lostAcks;

    // Sender-thread only. See the heartbeat in SendFrame: well inside Core's 3s watchdog.
    private long _lastFrameSentTicks;
    private static readonly long KeepaliveEveryTicks = Stopwatch.Frequency; // 1s

    // Save/load-state requests: a UI-thread button click can't call
    // LibretroCore.SaveState()/LoadState() directly (see those methods' own
    // caveat — only the thread driving the core may touch it), so it drops
    // a path here instead and RunLoop picks it up between core.Run() calls,
    // its own thread. At most one request of each kind pending at a time is
    // enough for a manual button click — a rapid double-click just
    // overwrites the pending path with the same one.
    private volatile string? _pendingSavePath;
    private volatile string? _pendingLoadPath;

    public event Action<string>? StatusChanged;

    public GbaSession(BrobotConnection connection, string corePath, string workingDirectory, string romPath)
    {
        _connection = connection;
        _corePath = corePath;
        _workingDirectory = workingDirectory;
        _romPath = romPath;
    }

    public bool IsRunning => _running;

    /// <summary>True only while a game is actually loaded and being emulated — unlike IsRunning, false if the ROM failed to load.</summary>
    public bool IsPlaying => _running && _core != null;
    public string RomPath => _romPath;

    /// <summary>Queues a save-state write to <paramref name="path"/>, picked up on RunLoop's next iteration. No-op if no game is running.</summary>
    public void RequestSaveState(string path) => _pendingSavePath = path;

    /// <summary>Queues a save-state restore from <paramref name="path"/>, picked up on RunLoop's next iteration. No-op if no game is running.</summary>
    public void RequestLoadState(string path) => _pendingLoadPath = path;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _hasSentFrame = false;
        ResetInFlight();
        _frameAvailable.Reset();
        _seq = 0;

        _connection.SendCommand("STREAM START");

        _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = "GbaSession-Sender" };
        _senderThread.Start();
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
        _senderThread?.Join(2000);
        _senderThread = null;

        _connection.SendCommand("STREAM STOP");
    }

    /// <summary>Called from BrobotConnection's read thread (via MainWindow's OnFrameReceived) when a FRAMEOK line arrives.</summary>
    public void OnFrameAck(ushort seq)
    {
        lock (_inFlightLock)
        {
            int index = _inFlight.FindIndex(f => (ushort)f.Seq == seq);
            if (index < 0)
            {
                return; // a stale ack for a frame already given up on
            }

            long elapsedMs = (Stopwatch.GetTimestamp() - _inFlight[index].Ticks) * 1000 / Stopwatch.Frequency;
            Interlocked.Add(ref _rttTotalMs, elapsedMs);
            Interlocked.Increment(ref _rttSampleCount);

            // Acks come back in order, so this one also settles anything
            // older that was still outstanding.
            _inFlight.RemoveRange(0, index + 1);
            _slots.Release(index + 1);
        }
    }

    /// <summary>
    /// Blocks until sending <paramref name="bytes"/> more keeps the bytes in
    /// flight within InFlightByteBudget (an empty pipe always admits a frame).
    /// Gives up after AckTimeout and sends anyway — a lost ack is handled by
    /// SenderLoop, this must not add a second way to wedge.
    /// </summary>
    private void WaitForByteBudget(int bytes)
    {
        if (!_connection.IsSerialConnected)
        {
            return; // WiFi/TCP: flow-controlled, see UsbAckTimeout's comment
        }

        long deadline = Stopwatch.GetTimestamp() + (long)(AckTimeout.TotalSeconds * Stopwatch.Frequency);
        while (_running && Stopwatch.GetTimestamp() < deadline)
        {
            lock (_inFlightLock)
            {
                int inFlightBytes = 0;
                foreach (var f in _inFlight)
                {
                    inFlightBytes += f.Bytes;
                }
                if (inFlightBytes == 0 || inFlightBytes + bytes <= InFlightByteBudget)
                {
                    return;
                }
            }
            Thread.Sleep(1);
        }
    }

    /// <summary>Forgets every outstanding frame and frees all slots (start of a session, or after a lost ack).</summary>
    private void ResetInFlight()
    {
        lock (_inFlightLock)
        {
            _inFlight.Clear();
            int deficit = MaxInFlight - _slots.CurrentCount;
            if (deficit > 0)
            {
                _slots.Release(deficit);
            }
        }
    }

    /// <summary>
    /// Forwards a joypad button's held state to the loaded core — see
    /// LibretroCore.SetButtonState. Called from MainWindow's keyboard hook
    /// callback (the UI thread), not this session's own thread; a no-op
    /// while the core hasn't loaded yet (the brief window right after
    /// Start(), before RunLoop's LibretroCore construction finishes).
    /// </summary>
    public void SetButtonState(int button, bool pressed)
    {
        _core?.SetButtonState(button, pressed);
    }

    private void RunLoop()
    {
        LibretroCore? core = null;
        try
        {
            core = new LibretroCore(_corePath, _workingDirectory);
            core.FrameReady += OnFrameReady;
            _core = core;

            if (!core.LoadGame(_romPath))
            {
                StatusChanged?.Invoke("Falha ao carregar a ROM.");
                return;
            }

            TimeSpan frameInterval = TimeSpan.FromSeconds(1.0 / (core.Fps > 1 ? core.Fps : DefaultFps));
            var clock = Stopwatch.StartNew();
            TimeSpan nextFrameAt = clock.Elapsed;
            int emulatedFrames = 0;
            var statsClock = Stopwatch.StartNew();

            while (_running)
            {
                if (_pendingSavePath is { } savePath)
                {
                    _pendingSavePath = null;
                    byte[]? state = core.SaveState();
                    if (state != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                        File.WriteAllBytes(savePath, state);
                        StatusChanged?.Invoke("Estado salvo.");
                    }
                    else
                    {
                        StatusChanged?.Invoke("Falha ao salvar estado.");
                    }
                }

                if (_pendingLoadPath is { } loadPath)
                {
                    _pendingLoadPath = null;
                    if (File.Exists(loadPath))
                    {
                        bool ok = core.LoadState(File.ReadAllBytes(loadPath));
                        StatusChanged?.Invoke(ok ? "Estado carregado." : "Falha ao carregar estado.");
                    }
                    else
                    {
                        StatusChanged?.Invoke("Nenhum estado salvo pra essa ROM ainda.");
                    }
                }

                core.Run();
                emulatedFrames++;
                PublishFrame();

                nextFrameAt += frameInterval;
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
                    // Measures what's actually happening during real
                    // gameplay (as opposed to Phase 0's synthetic-pattern
                    // benchmark) — average rows touched per sent frame is
                    // the number that tells whether row diffs alone are
                    // still enough once real content (especially scrolling,
                    // which touches nearly every row at once) is driving
                    // them, or whether RLE-within-a-row is worth adding.
                    int rttSamples = Interlocked.Exchange(ref _rttSampleCount, 0);
                    long rttTotal = Interlocked.Exchange(ref _rttTotalMs, 0);
                    double avgRtt = rttSamples > 0 ? (double)rttTotal / rttSamples : 0;
                    int lostAcks = Interlocked.Exchange(ref _lostAcks, 0);
                    int sentFrames = Interlocked.Exchange(ref _sentFrames, 0);
                    int rowsSent = Interlocked.Exchange(ref _rowsSent, 0);
                    int maxFrameBytes = Interlocked.Exchange(ref _maxFrameBytes, 0);
                    double avgRowsPerSent = sentFrames > 0 ? (double)rowsSent / sentFrames : 0;
                    StatusChanged?.Invoke(
                        $"Rodando — {emulatedFrames} emulados/s, {sentFrames} enviados/s, " +
                        $"{avgRowsPerSent:F0}/{OutHeight} linhas por envio, RTT médio {avgRtt:F0}ms, " +
                        $"{lostAcks} FRAMEOK perdido(s)/s, maior quadro {maxFrameBytes / 1024}KB");
                    emulatedFrames = 0;
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
            _core = null;
            core?.Dispose();
        }
    }

    /// <summary>
    /// Area-averaging downscale: each output pixel is the average of every
    /// source pixel that maps onto it, not just the single nearest one.
    /// specs/sender-gba.md called this the option to try first, over the
    /// simpler nearest-neighbour — confirmed worth it once nearest-neighbour
    /// shipped and GBA text (already small at native 240x160) came out
    /// visibly harder to read than it needed to be at 160x107: nearest-
    /// neighbour either keeps or drops a whole source pixel per output
    /// pixel with nothing in between, which for single-pixel-wide text
    /// strokes means a stroke randomly surviving or vanishing depending on
    /// where the sampling point happens to land; averaging blends it in
    /// instead, so a stroke that doesn't cleanly survive downscaling still
    /// shows up as a visible (if softer) partial pixel rather than nothing.
    /// </summary>
    private void OnFrameReady(LibretroCore.PixelFormat format, int width, int height, byte[] pixels)
    {
        int bytesPerPixel = format == LibretroCore.PixelFormat.Xrgb8888 ? 4 : 2;
        int rowBytes = width * bytesPerPixel;

        // Fit the picture inside Peemo's screen, keeping its aspect ratio, and
        // center it: whatever the core outputs (GBA 240x160, SNES 256x224 — or
        // 512x448 in SNES hi-res modes, same shape) ends up as scale x size on
        // black. The bars are simply rows/columns of _currentFrame nobody ever
        // writes, so they cost nothing to send once they're black.
        double scale = Math.Min((double)OutWidth / width, (double)OutHeight / height);
        int imageWidth = Math.Clamp((int)Math.Round(width * scale), 1, OutWidth);
        int imageHeight = Math.Clamp((int)Math.Round(height * scale), 1, OutHeight);
        int xOffset = (OutWidth - imageWidth) / 2;
        int yOffset = (OutHeight - imageHeight) / 2;

        if (imageWidth != _imageWidth || imageHeight != _imageHeight)
        {
            // Resolution changed (a SNES game switching video modes): the old
            // picture's edges could otherwise linger in what are now the bars.
            Array.Clear(_currentFrame);
            _imageWidth = imageWidth;
            _imageHeight = imageHeight;
        }

        for (int oy = 0; oy < imageHeight; oy++)
        {
            int sy0 = oy * height / imageHeight;
            int sy1 = Math.Max(sy0 + 1, (oy + 1) * height / imageHeight);
            for (int ox = 0; ox < imageWidth; ox++)
            {
                int sx0 = ox * width / imageWidth;
                int sx1 = Math.Max(sx0 + 1, (ox + 1) * width / imageWidth);
                _currentFrame[yOffset + oy, xOffset + ox] = AverageRegion(format, pixels, rowBytes, bytesPerPixel, sx0, sx1, sy0, sy1);
            }
        }
    }

    /// <summary>Averages every source pixel in [x0,x1) x [y0,y1) (8-bit per channel, more accurate than averaging already-quantized 5/6-bit values) and re-quantizes the result to RGB565.</summary>
    private static ushort AverageRegion(LibretroCore.PixelFormat format, byte[] pixels, int rowBytes, int bytesPerPixel, int x0, int x1, int y0, int y1)
    {
        int sumR = 0, sumG = 0, sumB = 0, count = 0;
        for (int sy = y0; sy < y1; sy++)
        {
            int rowOffset = sy * rowBytes;
            for (int sx = x0; sx < x1; sx++)
            {
                ReadRgb888(format, pixels, rowOffset + sx * bytesPerPixel, out int r, out int g, out int b);
                sumR += r;
                sumG += g;
                sumB += b;
                count++;
            }
        }

        int avgR = sumR / count;
        int avgG = sumG / count;
        int avgB = sumB / count;
        return (ushort)(((avgR >> 3) << 11) | ((avgG >> 2) << 5) | (avgB >> 3));
    }

    /// <summary>Expands whichever native format the core is using to full 8-bit-per-channel RGB (bit-replicated, not just left-shifted, so e.g. 5-bit white (0x1F) becomes 8-bit white (0xFF) instead of 0xF8).</summary>
    private static void ReadRgb888(LibretroCore.PixelFormat format, byte[] pixels, int offset, out int r, out int g, out int b)
    {
        switch (format)
        {
            case LibretroCore.PixelFormat.Rgb565:
            {
                ushort px = (ushort)(pixels[offset] | (pixels[offset + 1] << 8));
                int r5 = (px >> 11) & 0x1F;
                int g6 = (px >> 5) & 0x3F;
                int b5 = px & 0x1F;
                r = (r5 << 3) | (r5 >> 2);
                g = (g6 << 2) | (g6 >> 4);
                b = (b5 << 3) | (b5 >> 2);
                break;
            }

            case LibretroCore.PixelFormat.Xrgb8888:
                // Memory order is B,G,R,X (little-endian 0xXXRRGGBB) — see
                // LibretroCore's own pixel-format comment.
                b = pixels[offset];
                g = pixels[offset + 1];
                r = pixels[offset + 2];
                break;

            default: // Rgb1555 (0RGB1555)
            {
                ushort px = (ushort)(pixels[offset] | (pixels[offset + 1] << 8));
                int r5 = (px >> 10) & 0x1F;
                int g5 = (px >> 5) & 0x1F;
                int b5 = px & 0x1F;
                r = (r5 << 3) | (r5 >> 2);
                g = (g5 << 3) | (g5 >> 2);
                b = (b5 << 3) | (b5 >> 2);
                break;
            }
        }
    }

    /// <summary>RunLoop thread: hands the frame just emulated to the sender (overwriting one it never got to — that is the frame skipping).</summary>
    private void PublishFrame()
    {
        lock (_frameLock)
        {
            Buffer.BlockCopy(_currentFrame, 0, _publishedFrame, 0, OutHeight * OutWidth * sizeof(ushort));
        }
        _frameAvailable.Set();
    }

    /// <summary>
    /// Sender thread: waits for a published frame and for the previous FRAME's
    /// ack (one in flight), then sends whatever is the latest by then.
    /// </summary>
    private void SenderLoop()
    {
        while (_running)
        {
            if (!_frameAvailable.Wait(100))
            {
                continue;
            }

            if (!_slots.Wait(AckTimeout))
            {
                // The FRAMEOK for the oldest send never arrived — give up on
                // everything outstanding rather than stall here forever (see
                // AckTimeout's own comment).
                ResetInFlight();
                Interlocked.Increment(ref _lostAcks);
                _slots.Wait();
            }

            if (!_running)
            {
                break;
            }

            lock (_frameLock)
            {
                Buffer.BlockCopy(_publishedFrame, 0, _sendFrame, 0, OutHeight * OutWidth * sizeof(ushort));
                _frameAvailable.Reset();
            }

            int touchedRows = SendFrame();
            if (touchedRows > 0)
            {
                Interlocked.Increment(ref _sentFrames);
                Interlocked.Add(ref _rowsSent, touchedRows);
            }
        }
    }

    /// <summary>Returns the number of rows actually sent (0 if the link wasn't ready, or if nothing had changed).</summary>
    private int SendFrame()
    {
        if (!_connection.IsConnected)
        {
            _slots.Release(); // the slot taken in SenderLoop, unused
            return 0;
        }

        var touchedRows = new List<int>();
        for (int y = 0; y < OutHeight; y++)
        {
            bool changed = !_hasSentFrame;
            if (!changed)
            {
                for (int x = 0; x < OutWidth; x++)
                {
                    if (_sendFrame[y, x] != _lastSentFrame[y, x])
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
            // Nothing changed on screen (a pause menu, a dialog waiting for a
            // button, a still title screen). Core drops out of video mode if it
            // sees no FRAME for STREAM_TIMEOUT_MS (3s, Config.h) — it takes
            // silence to mean the PC app died — and once it has, the diffs that
            // follow when the picture moves again are ignored: the game keeps
            // running but Peemo never shows it again. So a still picture still
            // sends a heartbeat: one row, re-sent unchanged, about once a second.
            long sinceLastSend = Stopwatch.GetTimestamp() - _lastFrameSentTicks;
            if (!_hasSentFrame || sinceLastSend < KeepaliveEveryTicks)
            {
                _slots.Release();
                return 0;
            }
            touchedRows.Add(OutHeight / 2);
        }

        var records = new byte[touchedRows.Count][];
        int totalBytes = 0;
        for (int i = 0; i < touchedRows.Count; i++)
        {
            int row = touchedRows[i];
            byte[] record = EncodeRow(row);
            records[i] = record;
            totalBytes += record.Length;
            for (int x = 0; x < OutWidth; x++)
            {
                _lastSentFrame[row, x] = _sendFrame[row, x];
            }
        }

        byte[] payload = new byte[totalBytes];
        int offset = 0;
        foreach (byte[] record in records)
        {
            Buffer.BlockCopy(record, 0, payload, offset, record.Length);
            offset += record.Length;
        }

        _hasSentFrame = true;
        int seq = ++_seq;
        byte[] header = Encoding.ASCII.GetBytes($"FRAME {(ushort)seq} {payload.Length}\n");
        byte[] framed = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, framed, 0, header.Length);
        Buffer.BlockCopy(payload, 0, framed, header.Length, payload.Length);

        int previousMax;
        while (framed.Length > (previousMax = Volatile.Read(ref _maxFrameBytes))
               && Interlocked.CompareExchange(ref _maxFrameBytes, framed.Length, previousMax) != previousMax)
        {
        }
        WaitForByteBudget(framed.Length);
        lock (_inFlightLock)
        {
            _inFlight.Add((seq, Stopwatch.GetTimestamp(), framed.Length));
            _lastFrameSentTicks = Stopwatch.GetTimestamp();
        }
        _connection.SendRawBytes(framed);
        return touchedRows.Count;
    }

    /// <summary>
    /// Encodes one row of _sendFrame as a wire record — [row][encoding]
    /// [length, 16-bit BE][payload] (see PROTOCOL.md's STREAM/FRAME
    /// section). Tries run-length first and falls back to raw whenever RLE
    /// wouldn't actually be smaller (a row with no repeated pixels costs 3
    /// bytes/run instead of 2 bytes/pixel raw, so it's a real possibility,
    /// not just a formality) — this is what makes RLE a strict improvement
    /// over always-raw rather than a gamble on content.
    /// </summary>
    private byte[] EncodeRow(int localRow)
    {
        int wireRow = localRow;

        // Build the RLE candidate first since it tells us whether it's
        // even worth using.
        var rle = new List<byte>(RowMaxPayloadBytes);
        int x = 0;
        while (x < OutWidth)
        {
            ushort px = _sendFrame[localRow, x];
            int runLength = 1;
            while (x + runLength < OutWidth && runLength < 255 && _sendFrame[localRow, x + runLength] == px)
            {
                runLength++;
            }
            rle.Add((byte)runLength);
            rle.Add((byte)(px >> 8));
            rle.Add((byte)(px & 0xFF));
            x += runLength;
        }

        byte encoding;
        byte[] payload;
        if (rle.Count < RowMaxPayloadBytes)
        {
            encoding = RowEncodingRle;
            payload = rle.ToArray();
        }
        else
        {
            encoding = RowEncodingRaw;
            payload = new byte[RowMaxPayloadBytes];
            for (int i = 0; i < OutWidth; i++)
            {
                ushort px = _sendFrame[localRow, i];
                payload[i * 2] = (byte)(px >> 8);
                payload[i * 2 + 1] = (byte)(px & 0xFF);
            }
        }

        byte[] record = new byte[RowHeaderBytes + payload.Length];
        record[0] = (byte)wireRow;
        record[1] = encoding;
        record[2] = (byte)(payload.Length >> 8);
        record[3] = (byte)(payload.Length & 0xFF);
        Buffer.BlockCopy(payload, 0, record, RowHeaderBytes, payload.Length);
        return record;
    }

    public void Dispose() => Stop();
}
