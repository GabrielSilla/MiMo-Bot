using System.IO;

namespace Brobot.Sender.Gba;

/// <summary>
/// What differs between the consoles the Mini Games card can run: which
/// libretro core, which ROM extensions belong to it, and how the keyboard maps
/// onto its pad. Everything else (the stream pipeline, saves, the card) is
/// shared — a ROM's extension picks its profile, so the same card and the same
/// "JOGAR" button serve every console.
/// </summary>
internal sealed class ConsoleProfile
{
    public string Name { get; }
    public string CorePath { get; }
    public string[] Extensions { get; }
    /// <summary>Windows virtual-key code -> RETRO_DEVICE_ID_JOYPAD_* (LibretroCore's ButtonXxx).</summary>
    public IReadOnlyDictionary<int, int> KeyMap { get; }
    /// <summary>One line telling the player what the keys do, shown on the card.</summary>
    public string ControlsHelp { get; }

    private ConsoleProfile(string name, string corePath, string[] extensions, Dictionary<int, int> keyMap, string controlsHelp)
    {
        Name = name;
        CorePath = corePath;
        Extensions = extensions;
        KeyMap = keyMap;
        ControlsHelp = controlsHelp;
    }

    public static readonly ConsoleProfile Gba = new(
        "Game Boy Advance",
        CorePaths.Mgba,
        new[] { ".gba" },
        new Dictionary<int, int>
        {
            [0x26] = LibretroCore.ButtonUp,     // VK_UP
            [0x28] = LibretroCore.ButtonDown,   // VK_DOWN
            [0x25] = LibretroCore.ButtonLeft,   // VK_LEFT
            [0x27] = LibretroCore.ButtonRight,  // VK_RIGHT
            [0x5A] = LibretroCore.ButtonA,      // Z
            [0x58] = LibretroCore.ButtonB,      // X
            [0x41] = LibretroCore.ButtonL,      // A
            [0x53] = LibretroCore.ButtonR,      // S
            [0x0D] = LibretroCore.ButtonStart,  // Enter
            [0x08] = LibretroCore.ButtonSelect, // Backspace
        },
        "GBA: setinhas movem, Z=A, X=B, A=L, S=R, Enter=Start, Backspace=Select");

    // The SNES pad has four face buttons in a diamond; the keys are laid out
    // the same way on the keyboard: Z (bottom) = B, X (right) = A, A (left) =
    // Y, S (top) = X. Shoulders on Q/W, above the face buttons.
    public static readonly ConsoleProfile Snes = new(
        "Super Nintendo",
        CorePaths.Snes9x,
        new[] { ".sfc", ".smc", ".swc", ".fig" },
        new Dictionary<int, int>
        {
            [0x26] = LibretroCore.ButtonUp,
            [0x28] = LibretroCore.ButtonDown,
            [0x25] = LibretroCore.ButtonLeft,
            [0x27] = LibretroCore.ButtonRight,
            [0x5A] = LibretroCore.ButtonB,      // Z
            [0x58] = LibretroCore.ButtonA,      // X
            [0x41] = LibretroCore.ButtonY,      // A
            [0x53] = LibretroCore.ButtonX,      // S
            [0x51] = LibretroCore.ButtonL,      // Q
            [0x57] = LibretroCore.ButtonR,      // W
            [0x0D] = LibretroCore.ButtonStart,
            [0x08] = LibretroCore.ButtonSelect,
        },
        "SNES: setinhas movem, Z=B, X=A, A=Y, S=X, Q=L, W=R, Enter=Start, Backspace=Select");

    public static readonly ConsoleProfile[] All = { Gba, Snes };

    /// <summary>The console a ROM file belongs to, by extension; null for anything unrecognized.</summary>
    public static ConsoleProfile? ForRom(string romPath)
    {
        string extension = Path.GetExtension(romPath);
        return All.FirstOrDefault(p => p.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A ROM's file name as a game title: "Pokemon - FireRed Version (USA, Europe)
    /// (Rev 1).gba" -> "Pokemon - FireRed Version". Dump names carry region and
    /// revision tags in (parentheses)/[brackets] that mean nothing in a report.
    /// </summary>
    public static string GameTitle(string romPath)
    {
        string name = Path.GetFileNameWithoutExtension(romPath);
        string cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
        return cleaned.Length > 0 ? cleaned : name;
    }

    /// <summary>OpenFileDialog filter covering every supported console, then each on its own.</summary>
    public static string FileDialogFilter()
    {
        string Pattern(ConsoleProfile p) => string.Join(";", p.Extensions.Select(e => "*" + e));
        string all = string.Join(";", All.Select(Pattern));
        string each = string.Join("|", All.Select(p => $"{p.Name} ({Pattern(p)})|{Pattern(p)}"));
        return $"Todas as ROMs suportadas ({all})|{all}|{each}|Todos os arquivos (*.*)|*.*";
    }
}
