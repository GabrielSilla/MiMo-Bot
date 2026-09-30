using System.IO;

namespace Brobot.Sender.Gba;

/// <summary>
/// Where the bundled libretro cores live: <c>cores\</c> next to the running exe
/// (see the csproj), so a build or an installed copy always has them and nothing
/// depends on a download or on a file sitting in the user's AppData. The
/// per-user folder (%AppData%\Brobot\gba) is still where saves and states go —
/// Program Files isn't writable.
/// </summary>
internal static class CorePaths
{
    public static string Mgba => Path.Combine(AppContext.BaseDirectory, "cores", "mgba_libretro.dll");
    public static string Snes9x => Path.Combine(AppContext.BaseDirectory, "cores", "snes9x_libretro.dll");
}
