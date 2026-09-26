using System.Runtime.InteropServices;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// How long since the last mouse/keyboard input anywhere on the machine
/// (GetLastInputInfo) — Pensamentos holds its turn while the user is away,
/// so Peemo thinks when they come back instead of talking to an empty room.
/// </summary>
internal static class UserPresence
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan IdleTime
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero; // can't tell — assume present rather than never speaking
            }
            // Both are 32-bit tick counts, so the unsigned subtraction stays
            // right across the ~49.7-day wraparound.
            uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
    }
}
