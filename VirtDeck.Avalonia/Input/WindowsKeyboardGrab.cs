using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using VirtDeck.Diagnostics;

namespace VirtDeck.Avalonia.Input;

/// <summary>
/// Keyboard grab for Windows via a <c>WH_KEYBOARD_LL</c> hook. The shell acts on the Windows key
/// and on Alt+Tab before any window sees them, and the Start menu it opens takes focus, so the
/// guest gets the key down and never the key up.
///
/// Unlike the X11 grab this one is selective: it swallows only the keys the shell would take (both
/// Windows keys, and Tab, Esc, Space and F4 under Alt, Esc under Ctrl) and hands them back through
/// <see cref="KeyCaptured"/>. Every other key reaches Avalonia as usual, so text input, dead keys and
/// the local layout keep working for the RFB view, which needs them.
///
/// A key up is swallowed only when its key down was, so Windows is never left believing a key it
/// saw go down is still held. The hook runs on the thread that installed it, which is the UI
/// thread, so <see cref="KeyCaptured"/> fires in order with Avalonia's own key events.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsKeyboardGrab : IKeyboardGrab
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_ALTDOWN = 0x20;

    private const int VK_TAB = 0x09;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_F4 = 0x73;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelKeyboardProc _proc;   // kept alive: the hook holds a pointer to it
    private readonly HashSet<int> _swallowed = new();
    private IntPtr _hook;
    private IntPtr _hwnd;
    private bool _unsupported;
    private bool _disposed;

    public WindowsKeyboardGrab() => _proc = HookProc;

    public event Action<KeyEventArgs, bool>? KeyCaptured;

    public bool IsActive => _hook != IntPtr.Zero;

    public bool IsSupported => !_unsupported;

    public bool Grab(Window window)
    {
        if (_disposed || IsActive || _unsupported) return false;
        if (!Dispatcher.UIThread.CheckAccess()) return false;   // the hook runs on the installing thread

        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero) return false;
        if (!string.Equals(handle.HandleDescriptor, "HWND", StringComparison.Ordinal)) return false;

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _unsupported = true;
            SpiceLog.Log($"Windows keyboard grab unavailable: SetWindowsHookEx failed ({Marshal.GetLastWin32Error()}).");
            return false;
        }
        _hwnd = handle.Handle;
        return true;
    }

    public void Release()
    {
        if (!IsActive) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Release);
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hwnd = IntPtr.Zero;
        _swallowed.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hook != IntPtr.Zero && GetForegroundWindow() == _hwnd)
        {
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            bool swallow = (down && IsReserved(info)) || (up && _swallowed.Contains(info.vkCode));
            if (swallow && ToAvalonia(info.vkCode) is var (key, physical))
            {
                if (down) _swallowed.Add(info.vkCode);
                else _swallowed.Remove(info.vkCode);

                var args = new KeyEventArgs
                {
                    Key = key,
                    PhysicalKey = physical,
                    KeyModifiers = CurrentModifiers(info),
                };
                try { KeyCaptured?.Invoke(args, down); }
                catch (Exception ex) { SpiceLog.Log($"Captured key handler failed: {ex.Message}"); }
                return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>The keys the shell acts on before the focused window sees them.</summary>
    private static bool IsReserved(KBDLLHOOKSTRUCT info)
    {
        bool alt = (info.flags & LLKHF_ALTDOWN) != 0;
        bool ctrl = IsDown(VK_CONTROL);
        return info.vkCode switch
        {
            VK_LWIN or VK_RWIN => true,
            VK_TAB or VK_SPACE or VK_F4 => alt,
            VK_ESCAPE => alt || ctrl,
            _ => false,
        };
    }

    private static (Key, PhysicalKey)? ToAvalonia(int vk) => vk switch
    {
        VK_LWIN => (Key.LWin, PhysicalKey.MetaLeft),
        VK_RWIN => (Key.RWin, PhysicalKey.MetaRight),
        VK_TAB => (Key.Tab, PhysicalKey.Tab),
        VK_ESCAPE => (Key.Escape, PhysicalKey.Escape),
        VK_SPACE => (Key.Space, PhysicalKey.Space),
        VK_F4 => (Key.F4, PhysicalKey.F4),
        _ => null,
    };

    private KeyModifiers CurrentModifiers(KBDLLHOOKSTRUCT info)
    {
        var mods = KeyModifiers.None;
        if ((info.flags & LLKHF_ALTDOWN) != 0) mods |= KeyModifiers.Alt;
        if (IsDown(VK_CONTROL)) mods |= KeyModifiers.Control;
        if (IsDown(VK_SHIFT)) mods |= KeyModifiers.Shift;
        if (_swallowed.Contains(VK_LWIN) || _swallowed.Contains(VK_RWIN)) mods |= KeyModifiers.Meta;
        return mods;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
