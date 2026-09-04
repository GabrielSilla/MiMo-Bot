using System.Runtime.InteropServices;

namespace Brobot.Sender;

/// <summary>
/// A system-wide WH_KEYBOARD_LL hook shared by both of MiMo's minigames (the
/// Anti-Stress Pong card and the Batalha RPG card): while either is up, the
/// player is looking at MiMo's own screen, not this app's window, so control
/// has to work no matter what has focus on the PC. Each minigame installs
/// and owns its own instance (see MainWindow.xaml.cs's _pongHook/_rpgHook) —
/// this class itself has no notion of which game is using it. This was the
/// repo's first P/Invoke — nothing existing to build on here.
///
/// Must be installed from the UI thread. A low-level keyboard hook's
/// callback runs synchronously as part of that thread's own message pump —
/// that's the whole mechanism, Windows calls back into this process while
/// dispatching the next keyboard message — so unlike every other
/// background-thread callback in this app, this one does NOT need
/// Dispatcher.Invoke to touch UI/_connection: it's already on that thread.
///
/// Never suppresses a key (always calls CallNextHookEx) — arrows/Escape
/// still reach whatever window has focus on the PC. Deliberate simple
/// default for a fun feature; easy to change later if it ever proves
/// annoying in practice.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_RETURN = 0x0D;

    // Kept alive for the hook's whole lifetime: SetWindowsHookEx only hands
    // Windows a raw function pointer, not a reference to this delegate — with
    // no managed reference keeping it alive, the GC would be free to collect
    // it out from under an active hook.
    private readonly HookProc _hookProc;
    private nint _hookHandle;

    private bool _leftHeld;
    private bool _rightHeld;
    private bool _enterHeld;

    /// <summary>true = pressed, false = released. Only fires on an actual state transition (Windows repeats WM_KEYDOWN while a key is held).</summary>
    public event Action<bool>? LeftArrowChanged;
    public event Action<bool>? RightArrowChanged;
    public event Action? EscapePressed;

    /// <summary>
    /// Fires once per press (Enter), same transition-only treatment as the
    /// arrows — unlike EscapePressed, which fires on every OS auto-repeat
    /// while held (harmless there since Stop*Game methods are idempotent,
    /// but a menu's CONFIRM repeating while Enter is held a beat too long
    /// would silently confirm two menu screens in a row).
    /// </summary>
    public event Action? EnterPressed;

    public GlobalKeyboardHook()
    {
        // Stored once so it survives for the object's lifetime rather than a
        // fresh closure being handed to SetWindowsHookEx each Install() call.
        _hookProc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != 0)
        {
            return; // already installed
        }

        // hMod is NULL/IntPtr.Zero for a WH_KEYBOARD_LL hook whose procedure
        // lives in the calling process's own code, per SetWindowsHookEx's
        // documented behavior for low-level hooks specifically.
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
    }

    public void Dispose()
    {
        if (_hookHandle == 0)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = 0;
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            // KBDLLHOOKSTRUCT's first field is the DWORD vkCode.
            int vkCode = Marshal.ReadInt32(lParam);
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            if (isDown || isUp)
            {
                switch (vkCode)
                {
                    case VK_LEFT:
                        if (_leftHeld != isDown)
                        {
                            _leftHeld = isDown;
                            LeftArrowChanged?.Invoke(isDown);
                        }
                        break;
                    case VK_RIGHT:
                        if (_rightHeld != isDown)
                        {
                            _rightHeld = isDown;
                            RightArrowChanged?.Invoke(isDown);
                        }
                        break;
                    case VK_ESCAPE:
                        if (isDown)
                        {
                            EscapePressed?.Invoke();
                        }
                        break;
                    case VK_RETURN:
                        if (_enterHeld != isDown)
                        {
                            _enterHeld = isDown;
                            if (isDown)
                            {
                                EnterPressed?.Invoke();
                            }
                        }
                        break;
                }
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
}
