using System.IO;
using System.Text.Json;

namespace Brobot.Sender;

/// <summary>
/// Everything the "Salvar configurações" button persists: which checkboxes
/// were on, which AI provider was selected, and how to reach Core — so the
/// app comes back up already configured next launch instead of starting
/// from a blank checklist every time. Checkboxes still take effect
/// immediately when toggled (see MainWindow) — this only controls what's
/// remembered across restarts.
/// </summary>
public sealed class SenderSettings
{
    public bool HoraEnabled { get; set; }
    public bool ClimaEnabled { get; set; }
    public bool PausaEnabled { get; set; }
    public string PausaManha { get; set; } = "10:00";
    public string PausaTarde { get; set; } = "15:30";
    public string PensamentosIaProvider { get; set; } = "Claude";
    public bool MidiaEnabled { get; set; }
    public bool JogosEnabled { get; set; }
    public string Theme { get; set; } = ThemeManager.DefaultTheme;

    // Unlike the checkboxes above (opt-in monitors, off by default), Sons
    // and Scanlines toggle features MiMo already has on by default (see
    // DeviceSettings.h's SOUND/SCANLINES) — defaulting these to true keeps
    // a fresh install's checkboxes matching what the device already does,
    // instead of silently muting/flattening it the first time this settings
    // file is created.
    public bool SonsEnabled { get; set; } = true;
    public bool ScanlinesEnabled { get; set; } = true;

    // Whether the hidden "Modo teste" card has been unlocked (see
    // MainWindow's LogoImage_MouseLeftButtonUp). Persisted so the gesture is
    // a one-time thing per install rather than something to repeat on every
    // launch; it stays off for anyone who never performs it.
    public bool TestModeUnlocked { get; set; }

    // MiMo's IP on the local network — this app only ever reaches Core over
    // WiFi (see MainWindow's Conexão card); Brobot.Display.Simulator still
    // supports Serial, but this app doesn't need it.
    public string TcpHost { get; set; } = "";
    public int TcpPort { get; set; } = 5555;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "mimo-sender-settings.json");

    public static SenderSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<SenderSettings>(json) ?? new SenderSettings();
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings file — start fresh rather than crash the app.
        }

        return new SenderSettings();
    }

    public void Save()
    {
        string? dir = Path.GetDirectoryName(FilePath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
