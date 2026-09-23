using System.Runtime.InteropServices;

namespace Brobot.Sender;

public enum ResourceKind { Cpu, Ram }

/// <summary>
/// Always-on CPU/RAM watcher behind the "Alertas de desempenho" card —
/// deliberately separate from SystemStatsMonitor, which only runs while
/// GameMonitor reports a game (it exists for Game Mode's one screen) and
/// pulls in LibreHardwareMonitor's full hardware enumeration. An alert has
/// to work all day, so this reads the two numbers it needs straight from
/// Win32 instead: GetSystemTimes for CPU (the same idle/kernel/user split
/// Task Manager's total is computed from) and GlobalMemoryStatusEx's
/// dwMemoryLoad for RAM (the exact percentage Task Manager shows). Both
/// are a single cheap syscall, no driver, no elevation.
///
/// An alert fires only after the load has stayed at or above
/// <see cref="Threshold"/> for <see cref="SustainedSamples"/> consecutive
/// samples — CPU in particular spikes to 100% for a second or two all the
/// time (opening an app, a build starting), and nagging about every spike
/// would train people to ignore MiMo. After firing, the same resource stays
/// disarmed until it drops back under <see cref="RearmBelow"/> (hysteresis,
/// so hovering around 90% doesn't re-alert every 15s) *and*
/// <see cref="Cooldown"/> has passed since its last alert.
/// </summary>
public sealed class ResourceAlertMonitor : IDisposable
{
    public const int Threshold = 90;
    private const int RearmBelow = 80;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // 3 × 5s = 15s sustained.
    private const int SustainedSamples = 3;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private volatile bool _disposed;

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrevCpu;

    private readonly Tracker _cpu = new();
    private readonly Tracker _ram = new();

    /// <summary>Every sample (null = couldn't read). Raised on a thread-pool thread — callers touching UI must marshal.</summary>
    public event Action<int?, int?>? Sampled;

    /// <summary>Raised on a thread-pool thread when a resource has stayed at/above the threshold long enough.</summary>
    public event Action<ResourceKind, int>? AlertRaised;

    public void Start()
    {
        if (_timer != null || _disposed)
        {
            return;
        }
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    public void Dispose()
    {
        _disposed = true;
        System.Threading.Timer? timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    private void Poll()
    {
        try
        {
            int? cpu, ram;
            bool cpuAlert, ramAlert;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                cpu = ReadCpuPercent();
                ram = ReadRamPercent();
                DateTime now = DateTime.UtcNow;
                cpuAlert = cpu is { } c && _cpu.Observe(c, now);
                ramAlert = ram is { } r && _ram.Observe(r, now);
            }

            Sampled?.Invoke(cpu, ram);
            if (cpuAlert)
            {
                AlertRaised?.Invoke(ResourceKind.Cpu, cpu!.Value);
            }
            if (ramAlert)
            {
                AlertRaised?.Invoke(ResourceKind.Ram, ram!.Value);
            }
        }
        catch (Exception)
        {
            // Same contract as the other monitors: never take the tray app
            // down over a sample; the next tick tries again.
        }
    }

    /// <summary>Per-resource sustain/hysteresis/cooldown state — see the class comment.</summary>
    private sealed class Tracker
    {
        private int _consecutiveHigh;
        private bool _armed = true;
        private DateTime _lastAlertUtc = DateTime.MinValue;

        public bool Observe(int percent, DateTime nowUtc)
        {
            if (percent < RearmBelow)
            {
                _armed = true;
            }

            if (percent < Threshold)
            {
                _consecutiveHigh = 0;
                return false;
            }

            _consecutiveHigh++;
            if (!_armed || _consecutiveHigh < SustainedSamples || nowUtc - _lastAlertUtc < Cooldown)
            {
                return false;
            }

            _armed = false;
            _lastAlertUtc = nowUtc;
            return true;
        }
    }

    // GetSystemTimes' kernel time already includes idle time, so busy =
    // (kernel + user) - idle over the delta between two samples. The very
    // first call only primes the baseline.
    private int? ReadCpuPercent()
    {
        if (!GetSystemTimes(out FILETIME idleFt, out FILETIME kernelFt, out FILETIME userFt))
        {
            return null;
        }

        ulong idle = ToUInt64(idleFt), kernel = ToUInt64(kernelFt), user = ToUInt64(userFt);
        int? result = null;
        if (_hasPrevCpu)
        {
            ulong total = (kernel - _prevKernel) + (user - _prevUser);
            ulong idleDelta = idle - _prevIdle;
            if (total > 0)
            {
                result = (int)Math.Round(100.0 * (total - idleDelta) / total);
                result = Math.Clamp(result.Value, 0, 100);
            }
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _hasPrevCpu = true;
        return result;
    }

    private static int? ReadRamPercent()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (int)status.dwMemoryLoad : null;
    }

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
