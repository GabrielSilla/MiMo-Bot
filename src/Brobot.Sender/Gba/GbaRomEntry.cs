using System.IO;

namespace Brobot.Sender.Gba;

/// <summary>
/// One entry in the Mini Games GBA card's ROM dropdown — just a full path
/// plus the short label to actually show (PeemoComboBoxStyle's closed-box
/// display needs an ItemTemplate/DisplayName, a raw path would run off the
/// card). See SenderSettings.GbaRecentRoms for how the underlying list is
/// persisted.
/// </summary>
public sealed class GbaRomEntry
{
    public string FullPath { get; }
    public string DisplayName { get; }

    public GbaRomEntry(string fullPath)
    {
        FullPath = fullPath;
        DisplayName = Path.GetFileNameWithoutExtension(fullPath);
    }
}
