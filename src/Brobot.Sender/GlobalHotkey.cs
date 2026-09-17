using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Brobot.Sender;

[Flags]
public enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// One system-wide key combo via RegisterHotKey/WM_HOTKEY — a different
/// mechanism from GlobalKeyboardHook's WH_KEYBOARD_LL hook on purpose: that
/// one reads continuous raw arrow/Enter/Escape state for the two minigames,
/// installed only while one is up, and pays a per-keystroke cost
/// system-wide for as long as it's active. This is the opposite shape — one
/// discrete combo, Windows itself does the matching, and this process only
/// hears about it on an actual press — which is the right tool for a single
/// always-on shortcut like ALT+F10 that has nothing to do with either
/// minigame and must keep working even while Brobot.Sender is hidden in the
/// tray, not just while its window has focus.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    // Arbitrary but fixed per instance — WM_HOTKEY's wParam carries this
    // back, which is how WndProc tells one registered hotkey apart from
    // another if this class is ever instantiated more than once.
    private static int _nextId = 0xB001;

    private readonly int _id;
    private readonly HwndSource _source;

    public event Action? Pressed;

    /// <summary>False when RegisterHotKey couldn't claim the combo (e.g. another app already owns it) — best-effort, the caller decides whether/how to surface that.</summary>
    public bool IsRegistered { get; }

    public GlobalHotkey(Window window, HotkeyModifiers modifiers, uint virtualKey)
    {
        _id = _nextId++;

        // Forces the underlying Win32 HWND to exist right away rather than
        // waiting for the window to actually be shown — needed here since
        // the hotkey has to work even before/without Show() ever being
        // called on this window this session.
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Window has no HWND.");
        _source.AddHook(WndProc);

        IsRegistered = RegisterHotKey(handle, _id, (uint)modifiers, virtualKey);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WM_HOTKEY arrives through this window's own message pump — the
        // UI thread — same "already on that thread" reasoning
        // GlobalKeyboardHook's own header comment gives for its callback,
        // so Pressed's subscribers don't need Dispatcher.Invoke either.
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        UnregisterHotKey(_source.Handle, _id);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
