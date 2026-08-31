using LibreHardwareMonitor.Hardware;

namespace Brobot.Sender;

/// <summary>One sample of the machine's load. Any field is null when no source could supply it, which the display shows as "--" rather than inventing a number.</summary>
public sealed record SystemStatsReading(
    int? CpuLoadPercent,
    int? CpuTempC,
    int? GpuLoadPercent,
    int? GpuTempC,
    int? RamLoadPercent);

/// <summary>
/// Samples CPU/GPU/RAM load and temperature for MiMo's Game Mode, from two
/// sources with different guarantees:
///
/// - <b>LibreHardwareMonitorLib</b> supplies CPU load, RAM load, GPU load and
///   GPU temperature. None of those need a kernel driver — GPU figures come
///   from NVIDIA's own user-mode NVML — so these four always work, on any
///   machine, with no install and no elevation.
/// - <b>MSI Afterburner</b> (see <see cref="AfterburnerSensors"/>) supplies CPU
///   temperature, which nothing else here can: it lives in an MSR, readable
///   only from kernel mode, and LibreHardwareMonitor's own driver for that is
///   blocked by Windows' vulnerable-driver blocklist on this machine (measured:
///   every CPU temperature sensor reads empty even when elevated).
///
/// The split is deliberate rather than incidental — it keeps the feature whole
/// for someone who has never heard of Afterburner, and costs them exactly one
/// field. Never let a missing source become an exception: a monitor that
/// throws is worse than a monitor that reports null.
/// </summary>
public sealed class SystemStatsMonitor : IDisposable
{
    // Fast enough to read as live on a display that's meant to update
    // constantly, slow enough that polling hardware sensors stays background
    // noise while a game has the machine busy — which is the entire situation
    // this runs in.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private Computer? _computer;
    private System.Threading.Timer? _timer;
    private volatile bool _disposed;

    /// <summary>Raised on a thread-pool thread — callers touching UI must marshal (same contract as the other monitors here).</summary>
    public event Action<SystemStatsReading>? StatsUpdated;

    public void Start()
    {
        if (_timer != null || _disposed)
        {
            return;
        }

        // Timer fires the first sample immediately; Computer.Open() enumerates
        // hardware and takes a moment, so it happens on the pool thread rather
        // than blocking whoever started monitoring.
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        System.Threading.Timer? timer = _timer;
        _timer = null;
        timer?.Dispose();

        lock (_gate)
        {
            _computer?.Close();
            _computer = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    private void Poll()
    {
        try
        {
            SystemStatsReading reading = Read();
            if (!_disposed)
            {
                StatsUpdated?.Invoke(reading);
            }
        }
        catch (Exception)
        {
            // A sensor library throwing (hardware removed, driver reloaded,
            // GPU dropping off for a driver reset mid-game) must never take
            // down the tray app, and there's nothing useful to report — the
            // next tick two seconds from now tries again.
        }
    }

    private SystemStatsReading Read()
    {
        int? cpuLoad = null, gpuLoad = null, gpuTemp = null, ramLoad = null, cpuTemp = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return new SystemStatsReading(null, null, null, null, null);
            }

            _computer ??= OpenComputer();

            foreach (IHardware hardware in _computer.Hardware)
            {
                hardware.Update();

                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        cpuLoad ??= FindSensor(hardware, SensorType.Load, "CPU Total");
                        // Tried first even though it comes back empty here, so
                        // a machine whose driver does load gets the real
                        // reading without needing Afterburner at all.
                        cpuTemp ??= FindSensor(hardware, SensorType.Temperature, "CPU Package")
                                    ?? FindSensor(hardware, SensorType.Temperature, "Core Average");
                        break;

                    case HardwareType.Memory:
                        ramLoad ??= FindSensor(hardware, SensorType.Load, "Memory");
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        gpuLoad ??= FindSensor(hardware, SensorType.Load, "GPU Core");
                        gpuTemp ??= FindSensor(hardware, SensorType.Temperature, "GPU Core");
                        break;
                }
            }
        }

        // Only consulted for what's still missing — normally just CPU
        // temperature, but it stands in for any of them if a sensor drops out.
        if (cpuTemp == null || cpuLoad == null || gpuTemp == null || gpuLoad == null)
        {
            IReadOnlyDictionary<string, float>? afterburner = AfterburnerSensors.TryReadAll();
            if (afterburner != null)
            {
                cpuTemp ??= Round(afterburner, AfterburnerSensors.CpuTemperature);
                cpuLoad ??= Round(afterburner, AfterburnerSensors.CpuUsage);
                gpuTemp ??= Round(afterburner, AfterburnerSensors.GpuTemperature);
                gpuLoad ??= Round(afterburner, AfterburnerSensors.GpuUsage);
            }
        }

        return new SystemStatsReading(cpuLoad, cpuTemp, gpuLoad, gpuTemp, ramLoad);
    }

    private static Computer OpenComputer()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            // Everything else stays off on purpose: each enabled category is
            // more hardware to enumerate and poll every two seconds, and
            // nothing on screen comes from motherboard, storage or network.
        };
        computer.Open();
        return computer;
    }

    private static int? FindSensor(IHardware hardware, SensorType type, string name)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == type
                && string.Equals(sensor.Name, name, StringComparison.OrdinalIgnoreCase)
                && sensor.Value is { } value
                && !float.IsNaN(value))
            {
                return (int)Math.Round(value);
            }
        }

        return null;
    }

    private static int? Round(IReadOnlyDictionary<string, float> readings, string key) =>
        readings.TryGetValue(key, out float value) ? (int)Math.Round(value) : null;
}
