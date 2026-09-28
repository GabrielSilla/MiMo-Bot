using System.IO;
using System.Runtime.InteropServices;

namespace Brobot.Sender.Gba;

/// <summary>
/// Minimal P/Invoke wrapper around a libretro core's C ABI (mgba_libretro.dll
/// — see specs/sender-gba.md) — just enough surface to load a ROM, step
/// frames, and receive video. Loaded dynamically via <see cref="NativeLibrary"/>
/// rather than a static DllImport so the core's path can be anything (a dev
/// RetroArch install today, the installer's own copy later) instead of
/// needing to sit next to this exe.
///
/// One instance per loaded core, and at most one instance alive at a time:
/// every libretro core keeps its state in the DLL's own globals, not in any
/// struct this class owns, so two instances would really be one shared core
/// underneath. Not thread-safe beyond that — call every member from the
/// same thread you constructed it on, same as a raw C library would demand.
///
/// Struct/typedef shapes below mirror libretro.h exactly (see
/// https://github.com/libretro/libretro-common/blob/master/include/libretro.h);
/// values there are frozen for backward compatibility, so hardcoding them
/// here (rather than shipping the whole header) is safe.
/// </summary>
public sealed class LibretroCore : IDisposable
{
    private const uint RETRO_ENVIRONMENT_GET_CAN_DUPE = 3;
    private const uint RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY = 9;
    private const uint RETRO_ENVIRONMENT_SET_PIXEL_FORMAT = 10;
    private const uint RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY = 31;

    public enum PixelFormat
    {
        Rgb1555,
        Xrgb8888,
        Rgb565,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroSystemInfo
    {
        public IntPtr LibraryName;
        public IntPtr LibraryVersion;
        public IntPtr ValidExtensions;
        [MarshalAs(UnmanagedType.U1)] public bool NeedFullpath;
        [MarshalAs(UnmanagedType.U1)] public bool BlockExtract;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroGameGeometry
    {
        public uint BaseWidth;
        public uint BaseHeight;
        public uint MaxWidth;
        public uint MaxHeight;
        public float AspectRatio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroSystemTiming
    {
        public double Fps;
        public double SampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroSystemAvInfo
    {
        public RetroGameGeometry Geometry;
        public RetroSystemTiming Timing;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RetroGameInfo
    {
        public IntPtr Path;
        public IntPtr Data;
        public UIntPtr Size;
        public IntPtr Meta;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VideoRefreshDelegate(IntPtr data, uint width, uint height, UIntPtr pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioSampleDelegate(short left, short right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate UIntPtr AudioSampleBatchDelegate(IntPtr data, UIntPtr frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InputPollDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate short InputStateDelegate(uint port, uint device, uint index, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool EnvironmentDelegate(uint cmd, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint RetroApiVersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroInitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroDeinitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroGetSystemInfoFn(out RetroSystemInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroGetSystemAvInfoFn(out RetroSystemAvInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroSetEnvironmentFn(EnvironmentDelegate cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroSetVideoRefreshFn(VideoRefreshDelegate cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroSetAudioSampleFn(AudioSampleDelegate cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroSetAudioSampleBatchFn(AudioSampleBatchDelegate cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroSetInputPollFn(InputPollDelegate cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroSetInputStateFn(InputStateDelegate cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool RetroLoadGameFn(ref RetroGameInfo game);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroUnloadGameFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RetroRunFn();

    private readonly nint _libraryHandle;
    private readonly RetroInitFn _retroInit;
    private readonly RetroDeinitFn _retroDeinit;
    private readonly RetroGetSystemInfoFn _retroGetSystemInfo;
    private readonly RetroGetSystemAvInfoFn _retroGetSystemAvInfo;
    private readonly RetroLoadGameFn _retroLoadGame;
    private readonly RetroUnloadGameFn _retroUnloadGame;
    private readonly RetroRunFn _retroRun;

    // Kept as fields (not locals) so the GC never collects them while the
    // core might still call back into native code holding their function
    // pointers — the classic P/Invoke delegate-lifetime pitfall.
    private readonly EnvironmentDelegate _environmentCb;
    private readonly VideoRefreshDelegate _videoRefreshCb;
    private readonly AudioSampleDelegate _audioSampleCb;
    private readonly AudioSampleBatchDelegate _audioSampleBatchCb;
    private readonly InputPollDelegate _inputPollCb;
    private readonly InputStateDelegate _inputStateCb;

    // Native strings handed out to GET_SYSTEM_DIRECTORY/GET_SAVE_DIRECTORY —
    // allocated once and kept alive for the object's lifetime, since the
    // core only reads the pointer's value when it asks, not what backs it.
    private readonly nint _systemDirectoryPtr;
    private readonly nint _saveDirectoryPtr;

    private bool _gameLoaded;
    private bool _disposed;

    public delegate void FrameReadyHandler(PixelFormat format, int width, int height, byte[] pixels);

    /// <summary>Raised synchronously from within Run(), once per retro_run() call — see Run()'s own comment.</summary>
    public event FrameReadyHandler? FrameReady;

    /// <summary>The pixel format the core announced via SET_PIXEL_FORMAT — valid only once a game is loaded.</summary>
    public PixelFormat CurrentPixelFormat { get; private set; } = PixelFormat.Rgb1555;

    /// <param name="corePath">Path to the libretro core DLL (e.g. mgba_libretro.dll).</param>
    /// <param name="workingDirectory">
    /// Where the core is told to keep system/save files (BIOS overrides,
    /// .sav) — see the environment callback below. Created if missing.
    /// </param>
    public LibretroCore(string corePath, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        _systemDirectoryPtr = Marshal.StringToHGlobalAnsi(workingDirectory);
        _saveDirectoryPtr = Marshal.StringToHGlobalAnsi(workingDirectory);

        _libraryHandle = NativeLibrary.Load(corePath);

        _retroInit = GetDelegate<RetroInitFn>("retro_init");
        _retroDeinit = GetDelegate<RetroDeinitFn>("retro_deinit");
        _retroGetSystemInfo = GetDelegate<RetroGetSystemInfoFn>("retro_get_system_info");
        _retroGetSystemAvInfo = GetDelegate<RetroGetSystemAvInfoFn>("retro_get_system_av_info");
        var retroSetEnvironment = GetDelegate<RetroSetEnvironmentFn>("retro_set_environment");
        var retroSetVideoRefresh = GetDelegate<RetroSetVideoRefreshFn>("retro_set_video_refresh");
        var retroSetAudioSample = GetDelegate<RetroSetAudioSampleFn>("retro_set_audio_sample");
        var retroSetAudioSampleBatch = GetDelegate<RetroSetAudioSampleBatchFn>("retro_set_audio_sample_batch");
        var retroSetInputPoll = GetDelegate<RetroSetInputPollFn>("retro_set_input_poll");
        var retroSetInputState = GetDelegate<RetroSetInputStateFn>("retro_set_input_state");
        _retroLoadGame = GetDelegate<RetroLoadGameFn>("retro_load_game");
        _retroUnloadGame = GetDelegate<RetroUnloadGameFn>("retro_unload_game");
        _retroRun = GetDelegate<RetroRunFn>("retro_run");

        _environmentCb = OnEnvironment;
        _videoRefreshCb = OnVideoRefresh;
        _audioSampleCb = (_, _) => { }; // batch below is what mGBA actually uses; this one just needs to exist
        _audioSampleBatchCb = OnAudioSampleBatch;
        _inputPollCb = () => { };
        _inputStateCb = (_, _, _, _) => 0; // no input wired up yet — see specs/sender-gba.md Phase 1

        retroSetEnvironment(_environmentCb);
        retroSetVideoRefresh(_videoRefreshCb);
        retroSetAudioSample(_audioSampleCb);
        retroSetAudioSampleBatch(_audioSampleBatchCb);
        retroSetInputPoll(_inputPollCb);
        retroSetInputState(_inputStateCb);

        _retroInit();
    }

    private T GetDelegate<T>(string exportName) where T : Delegate
    {
        nint fn = NativeLibrary.GetExport(_libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    /// <summary>
    /// Loads a ROM. Reads retro_get_system_info() first to find out whether
    /// this core wants the file path (need_fullpath) or the raw bytes —
    /// mGBA accepts a path, but checking rather than assuming is what keeps
    /// this wrapper correct for whichever core is pointed at it later.
    /// </summary>
    public bool LoadGame(string romPath)
    {
        _retroGetSystemInfo(out RetroSystemInfo systemInfo);

        nint pathPtr = Marshal.StringToHGlobalAnsi(romPath);
        nint dataPtr = IntPtr.Zero;
        byte[]? romBytes = null;
        try
        {
            var game = new RetroGameInfo { Path = pathPtr, Data = IntPtr.Zero, Size = UIntPtr.Zero, Meta = IntPtr.Zero };

            if (!systemInfo.NeedFullpath)
            {
                romBytes = File.ReadAllBytes(romPath);
                dataPtr = Marshal.AllocHGlobal(romBytes.Length);
                Marshal.Copy(romBytes, 0, dataPtr, romBytes.Length);
                game.Data = dataPtr;
                game.Size = (UIntPtr)romBytes.Length;
            }

            _gameLoaded = _retroLoadGame(ref game);
            return _gameLoaded;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
            if (dataPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    /// <summary>
    /// Steps exactly one emulated frame. Synchronous, and — like the native
    /// core itself — not paced to real time at all; the caller decides how
    /// often to call this (see specs/sender-gba.md's GbaSession, not built
    /// yet: real playback needs to pace this off the audio callback, not a
    /// plain loop). FrameReady fires synchronously from inside this call,
    /// on this thread, once retro_run()'s video_refresh callback lands.
    /// </summary>
    public void Run()
    {
        if (!_gameLoaded)
        {
            throw new InvalidOperationException("No game loaded.");
        }
        _retroRun();
    }

    private bool OnEnvironment(uint cmd, IntPtr data)
    {
        switch (cmd)
        {
            case RETRO_ENVIRONMENT_GET_CAN_DUPE:
                Marshal.WriteByte(data, 1);
                return true;

            case RETRO_ENVIRONMENT_SET_PIXEL_FORMAT:
                int fmt = Marshal.ReadInt32(data);
                CurrentPixelFormat = fmt switch
                {
                    1 => PixelFormat.Xrgb8888,
                    2 => PixelFormat.Rgb565,
                    _ => PixelFormat.Rgb1555,
                };
                return true;

            case RETRO_ENVIRONMENT_GET_SYSTEM_DIRECTORY:
                Marshal.WriteIntPtr(data, _systemDirectoryPtr);
                return true;

            case RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY:
                Marshal.WriteIntPtr(data, _saveDirectoryPtr);
                return true;

            default:
                // Every other RETRO_ENVIRONMENT_* command reports
                // "unsupported" — the standard, safe answer for a command
                // this minimal frontend has no opinion about; cores are
                // written to fall back to sane defaults when this happens.
                return false;
        }
    }

    private void OnVideoRefresh(IntPtr data, uint width, uint height, UIntPtr pitch)
    {
        if (FrameReady is null || data == IntPtr.Zero || width == 0 || height == 0)
        {
            return;
        }

        int bytesPerPixel = CurrentPixelFormat == PixelFormat.Xrgb8888 ? 4 : 2;
        int rowBytes = (int)width * bytesPerPixel;
        int stride = (int)pitch;
        var pixels = new byte[rowBytes * (int)height];

        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(data + y * stride, pixels, y * rowBytes, rowBytes);
        }

        FrameReady(CurrentPixelFormat, (int)width, (int)height, pixels);
    }

    private UIntPtr OnAudioSampleBatch(IntPtr data, UIntPtr frames)
    {
        // Audio isn't wired up yet (see specs/sender-gba.md Phase 1) — the
        // core still needs this callback to exist and to report every
        // frame as "consumed", or it may stall waiting for room to write
        // more samples.
        return frames;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_gameLoaded)
        {
            _retroUnloadGame();
            _gameLoaded = false;
        }
        _retroDeinit();

        if (_systemDirectoryPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_systemDirectoryPtr);
        }
        if (_saveDirectoryPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_saveDirectoryPtr);
        }

        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
        }
    }
}
