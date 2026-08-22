using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brobot.Sender;

/// <summary>
/// Detects whether a known game is currently running by polling the process
/// list against Discord's public "detectable applications" catalog — the
/// same executable-name database Discord's own client uses to show "Playing
/// X" activity status. Plain unauthenticated GET, no API key/login needed
/// (same zero-friction reasoning as WeatherMonitor picking Open-Meteo), and
/// it covers several thousand Windows game executables instead of a
/// hand-picked handful — there's no OS-level "this process is a game" flag
/// to query directly (see CLAUDE.md/conversation history), so riding
/// Discord's community-fed catalog is the closest thing to a generic answer.
/// The filtered (win32, non-launcher) executable map is cached to disk so
/// the app works offline after the first successful fetch and doesn't
/// redownload the ~12MB payload on every launch.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    private const string DetectableGamesUrl = "https://discord.com/api/v10/applications/detectable";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);
    private static readonly HttpClient HttpClient = new();

    // Bump whenever the filtering logic in RefreshFromNetworkAsync changes —
    // a cache file written by an older version (e.g. one that didn't exclude
    // generic runtime hosts like "dotnet.exe") is otherwise indistinguishable
    // from a fresh one and would keep being trusted for its full 7-day TTL
    // even after the bug that produced it was fixed. A missing/old Version
    // (older files simply don't have this field, so it deserializes as 0)
    // forces an immediate re-fetch regardless of age.
    private const int CacheSchemaVersion = 2;

    private static string CacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Brobot", "detectable-games-cache.json");

    // Generic language-runtime/interpreter hosts the catalog sometimes lists
    // as if they were a specific game's own binary (see RefreshFromNetworkAsync).
    // These are never themselves a game's identity — dozens of unrelated
    // programs share the same runtime host — so they're excluded outright
    // regardless of which game the catalog claims owns them.
    private static readonly HashSet<string> GenericRuntimeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "java", "javaw", "javaws", "python", "python3", "pythonw",
        "node", "ruby", "perl", "php", "mono", "mono-sgen", "wine", "wine64",
        "cmd", "powershell", "pwsh", "explorer", "conhost", "electron",
        "chrome", "chromium", "msedge", "firefox", "rundll32", "wscript", "cscript",
        "sh", "bash", "busybox", "busybox64",
    };

    private CancellationTokenSource? _cts;
    private string? _lastRaised;
    private Dictionary<string, string> _knownGames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _cacheFetchedAt;

    /// <summary>Raised (off the UI thread) with the game's display name, or null when no known game is running.</summary>
    public event Action<string?>? GameChanged;

    /// <summary>Human-readable progress/error text, for a status label.</summary>
    public event Action<string>? StatusChanged;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _lastRaised = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken token)
    {
        _knownGames = LoadCache() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool needsRefresh = _knownGames.Count == 0
            || !_cacheFetchedAt.HasValue
            || DateTime.UtcNow - _cacheFetchedAt.Value > CacheMaxAge;

        if (needsRefresh)
        {
            await RefreshFromNetworkAsync(token);
        }
        else
        {
            StatusChanged?.Invoke($"{_knownGames.Count} jogos catalogados (cache)");
        }

        while (!token.IsCancellationRequested)
        {
            Poll();

            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshFromNetworkAsync(CancellationToken token)
    {
        try
        {
            StatusChanged?.Invoke("Baixando catálogo de jogos...");

            using Stream stream = await HttpClient.GetStreamAsync(DetectableGamesUrl, token);
            List<DetectableApp>? apps = await JsonSerializer.DeserializeAsync<List<DetectableApp>>(stream, cancellationToken: token);

            // First pass: which game(s) claim each executable basename. The
            // catalog is community-submitted and has real data-quality bugs —
            // e.g. tModLoader's entry lists "dotnet.exe" (the .NET runtime
            // host, not a game-specific binary) as one of its executables,
            // which would otherwise report "playing tModLoader" on any dev
            // machine the instant anything else is running via dotnet.
            var claims = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (DetectableApp app in apps ?? [])
            {
                if (string.IsNullOrEmpty(app.Name) || app.Executables == null)
                {
                    continue;
                }

                foreach (DetectableExecutable exe in app.Executables)
                {
                    // is_launcher entries (e.g. LeagueClientUx.exe) are the menu/launcher
                    // process, not the actual match/session — skip them so "the launcher
                    // is open" doesn't get reported as "playing the game".
                    if (exe.Os != "win32" || exe.IsLauncher || string.IsNullOrEmpty(exe.Name))
                    {
                        continue;
                    }

                    // Some entries include a relative path ("win64/valorant-win64-shipping.exe") —
                    // Process.ProcessName is just the bare file name, so match on that alone.
                    string baseName = Path.GetFileNameWithoutExtension(exe.Name.Replace('/', '\\'));
                    if (baseName.Length == 0 || GenericRuntimeHosts.Contains(baseName))
                    {
                        continue;
                    }

                    if (!claims.TryGetValue(baseName, out HashSet<string>? owners))
                    {
                        owners = new HashSet<string>(StringComparer.Ordinal);
                        claims[baseName] = owners;
                    }

                    owners.Add(app.Name);
                }
            }

            // Second pass: an executable name claimed by more than one distinct
            // game is itself a sign of a generic/shared binary name (the same
            // problem the runtime-host list above guards against, just found
            // from the data instead of hardcoded) — drop those too rather than
            // arbitrarily picking whichever game happened to come first.
            var games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string baseName, HashSet<string> owners) in claims)
            {
                if (owners.Count == 1)
                {
                    games[baseName] = owners.First();
                }
            }

            _knownGames = games;
            _cacheFetchedAt = DateTime.UtcNow;
            SaveCache(games);
            StatusChanged?.Invoke($"{games.Count} jogos catalogados");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke(_knownGames.Count > 0
                ? $"Falha ao atualizar catálogo ({_knownGames.Count} jogos salvos localmente)"
                : $"Falha ao baixar catálogo de jogos: {ex.Message}");
        }
    }

    private Dictionary<string, string>? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                return null;
            }

            string json = File.ReadAllText(CacheFilePath);
            GameCache? cache = JsonSerializer.Deserialize<GameCache>(json);
            if (cache == null || cache.Version != CacheSchemaVersion)
            {
                return null; // missing/outdated schema — force a fresh fetch instead of trusting stale filtering
            }

            _cacheFetchedAt = cache.FetchedAt;
            return new Dictionary<string, string>(cache.Games, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null; // corrupt/unreadable cache — refetch instead
        }
    }

    private static void SaveCache(Dictionary<string, string> games)
    {
        try
        {
            string? dir = Path.GetDirectoryName(CacheFilePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(new GameCache(CacheSchemaVersion, DateTime.UtcNow, games)));
        }
        catch (Exception)
        {
            // Best-effort — a failed cache write just means next launch re-downloads.
        }
    }

    private void Poll()
    {
        string? found = null;

        // GetProcesses() hands out one handle per process; each must be
        // disposed once we're done reading its name or the handles leak.
        // A handful of protected/system processes throw when their name is
        // queried without elevated rights — caught per-process so one
        // inaccessible process doesn't kill the polling loop for the rest of
        // the app's lifetime (it did once: an unhandled exception here
        // silently stopped RunAsync's loop, leaving GameChanged stuck
        // reporting the last game it saw forever, even long after it closed).
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (found == null && _knownGames.TryGetValue(process.ProcessName, out string? displayName))
                    {
                        found = displayName;
                    }
                }
                catch (Exception)
                {
                    // Inaccessible process — skip it and keep going.
                }
            }
        }

        if (found == _lastRaised)
        {
            return;
        }

        _lastRaised = found;
        GameChanged?.Invoke(found);
    }

    private sealed record DetectableExecutable(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("os")] string Os,
        [property: JsonPropertyName("is_launcher")] bool IsLauncher);

    private sealed record DetectableApp(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("executables")] List<DetectableExecutable>? Executables);

    private sealed record GameCache(int Version, DateTime FetchedAt, Dictionary<string, string> Games);
}
