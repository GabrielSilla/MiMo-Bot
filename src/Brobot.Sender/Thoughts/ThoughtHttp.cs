using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Shared HTTP client and small on-disk cache helpers for the Pensamentos
/// sources that fetch something (facts, "neste dia", satellite orbits).
/// Every one of these is a free, keyless public endpoint (project rule: no
/// metered/keyed APIs), and all of them are only ever called from a
/// source's background refresh — TryPick never waits on the network.
/// </summary>
internal static class ThoughtHttp
{
    // Wikimedia's API policy asks for an identifying User-Agent with a way
    // to reach the maintainer; the public repo is that way.
    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PeemoSender/1.3 (+https://github.com/GabrielSilla/MiMo-Bot)");
        return client;
    }

    public static string CachePath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Brobot", fileName);

    public static T? LoadJson<T>(string fileName) where T : class
    {
        try
        {
            string path = CachePath(fileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null; // corrupt cache — refetch
        }
    }

    public static void SaveJson<T>(string fileName, T value)
    {
        try
        {
            string path = CachePath(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (Exception)
        {
            // Best-effort, same as every other cache file here.
        }
    }
}

/// <summary>A source with something to fetch in the background; MainWindow calls Start once.</summary>
internal interface IBackgroundThoughtSource
{
    void Start();
}
