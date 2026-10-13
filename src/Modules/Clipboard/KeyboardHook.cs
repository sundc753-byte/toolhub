using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipboardApp;

/// <summary>
/// Low-level keyboard hook that intercepts Win+V (which the shell owns, so
/// RegisterHotKey cannot claim it) and swallows it.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc; // keep alive for the native hook
    private IntPtr _hook;
    private bool _swallowedDown;

    public event Action? WinVPressed;

    public KeyboardHook()
    {
        _proc = HookCallback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo != Native.InjectedTag && k.vkCode == Native.VK_V)
            {
                int msg = (int)wParam;
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

                if (down && (_swallowedDown || IsPlainWinV()))
                {
                    // Auto-repeat while held is swallowed too, but only fires once.
                    if (!_swallowedDown)
                    {
                        _swallowedDown = true;
                        Native.SendDummyKey();
                        WinVPressed?.Invoke();
                    }
                    return (IntPtr)1;
                }

                if (up && _swallowedDown)
                {
                    _swallowedDown = false;
                    return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsPlainWinV() =>
        (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN)) &&
        !Native.IsDown(Native.VK_CONTROL) &&
        !Native.IsDown(Native.VK_MENU) &&
        !Native.IsDown(Native.VK_SHIFT);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
