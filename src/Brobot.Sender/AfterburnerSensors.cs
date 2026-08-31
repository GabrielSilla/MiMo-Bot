using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Brobot.Sender;

/// <summary>
/// Reads MSI Afterburner's published sensor values out of its
/// "MAHMSharedMemory" shared-memory block. Opportunistic by design: if
/// Afterburner isn't running the block doesn't exist, every read returns null,
/// and nothing else about MiMo changes.
///
/// This exists for exactly one metric that has no other source here: **CPU
/// temperature**. Intel keeps the die temperature in an MSR, which is readable
/// only from kernel mode, so *every* route to it runs through some kernel
/// driver. The usual library route (LibreHardwareMonitorLib) needs one of its
/// own — WinRing0, which Windows' vulnerable-driver blocklist now blocks, or
/// its signed successor PawnIO, which is a separate install. Measured on this
/// machine, both the WMI thermal zone and its performance-counter twin report
/// a fixed 27.9 C that doesn't move under full load, so neither is a real
/// reading. Afterburner already installed a working driver at its own install
/// time, so borrowing its numbers costs nothing and installs nothing — the
/// only catch being that it has to be running.
///
/// Everything else MiMo shows (CPU load, RAM, GPU load, GPU temperature) comes
/// from sources that always work, so a closed Afterburner costs one field, not
/// the feature.
/// </summary>
public sealed class AfterburnerSensors
{
    private const string SharedMemoryName = "MAHMSharedMemory";

    /// <summary>The header's own magic number, read off a live block rather than derived from the characters — the bytes in memory spell "MHAM", not "MAHM".</summary>
    private const uint ExpectedSignature = 0x4D41484D;

    /// <summary>What the signature becomes while Afterburner is shutting down; the values behind it are no longer being updated.</summary>
    private const uint ShuttingDownSignature = 0xDEAD;

    // MAHM_SHARED_MEMORY_ENTRY opens with five MAX_PATH char arrays
    // (szSrcName, szSrcUnits, szLocalizedSrcName, szLocalizedSrcUnits,
    // szRecommendedFormat) before the float value. Only the first and the
    // float are read here. Confirmed against a live block: header 32 bytes,
    // entries 1324 bytes, 58 entries.
    private const int MaxPath = 260;
    private const int EntryValueOffset = MaxPath * 5;

    // Entries are matched on szSrcName, not the localized name beside it —
    // szSrcName stays English ("CPU temperature") whatever language
    // Afterburner's own UI is set to, which on this machine is Portuguese.
    public const string CpuTemperature = "CPU temperature";
    public const string CpuUsage = "CPU usage";
    public const string GpuTemperature = "GPU temperature";
    public const string GpuUsage = "GPU usage";
    public const string Framerate = "Framerate";

    /// <summary>
    /// Every sensor Afterburner is currently publishing, keyed by szSrcName,
    /// or null when it isn't running. Read in one pass rather than per-sensor:
    /// the block is a single mapping and the caller wants several values from
    /// the same instant anyway.
    /// </summary>
    public static IReadOnlyDictionary<string, float>? TryReadAll()
    {
        try
        {
            using MemoryMappedFile map = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            using MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            uint signature = view.ReadUInt32(0);
            if (signature == ShuttingDownSignature || signature != ExpectedSignature)
            {
                // Either Afterburner is on its way out and has stopped
                // updating these values, or this isn't a block we understand.
                return null;
            }

            uint headerSize = view.ReadUInt32(8);
            uint entryCount = view.ReadUInt32(12);
            uint entrySize = view.ReadUInt32(16);

            // Walked using the header's own sizes rather than a compiled-in
            // struct layout, so a future Afterburner that grows its entries
            // reads correctly instead of returning garbage.
            if (entryCount == 0 || entrySize < EntryValueOffset + sizeof(float))
            {
                return null;
            }

            var readings = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var nameBuffer = new byte[MaxPath];

            for (uint i = 0; i < entryCount; i++)
            {
                long entryBase = headerSize + (long)i * entrySize;

                view.ReadArray(entryBase, nameBuffer, 0, MaxPath);
                string name = ReadNullTerminated(nameBuffer);
                if (name.Length == 0)
                {
                    continue;
                }

                float value = view.ReadSingle(entryBase + EntryValueOffset);
                if (!IsUsable(value))
                {
                    continue;
                }

                // First entry wins: per-GPU sensors repeat their names on a
                // multi-GPU machine, and the first is the primary adapter.
                readings.TryAdd(name, value);
            }

            return readings;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            // Not running, or running as a user this process can't read from.
            return null;
        }
    }

    /// <summary>
    /// Afterburner writes FLT_MAX into sensors it has no value for — Framerate
    /// reads that way whenever no game is running. Left unchecked it reaches
    /// the display as 340282346638528860000000000000000000000, so anything
    /// not finite and plausibly a sensor reading is treated as absent.
    /// </summary>
    private static bool IsUsable(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value) && value < float.MaxValue / 2 && value > -1000f;

    private static string ReadNullTerminated(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer, 0, end).Trim();
    }
}
