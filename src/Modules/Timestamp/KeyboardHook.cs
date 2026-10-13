using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolHub.Modules.Timestamp;

/// <summary>
/// Low-level keyboard hook that swallows Win+; (RegisterHotKey cannot be used
/// because the shell already owns that combination for the emoji panel).
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint LLKHF_INJECTED = 0x10;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_OEM_1 = 0xBA, VK_DUMMY = 0xE8;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    public event Action? WinSemicolonPressed;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private readonly HookProc _proc; // keep alive
    private IntPtr _hookId;

    public KeyboardHook() => _proc = Callback;

    public void Install()
    {
        using var p = Process.GetCurrentProcess();
        using var m = p.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(m.ModuleName), 0);
        if (_hookId == IntPtr.Zero) throw new InvalidOperationException("키보드 후크 설치 실패");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected && info.vkCode == VK_OEM_1 && WinDown())
            {
                WinSemicolonPressed?.Invoke();
                return (IntPtr)1; // swallow so the emoji panel doesn't open
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool WinDown() =>
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    /// <summary>Types text into the focused window via Unicode key events.</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();

        // Neutral key tap so releasing Win afterwards doesn't open the Start menu.
        inputs.Add(Key(VK_DUMMY, 0));
        inputs.Add(Key(VK_DUMMY, KEYEVENTF_KEYUP));
        // Release Win so the typed characters aren't interpreted as Win+<char>.
        inputs.Add(Key(VK_LWIN, KEYEVENTF_KEYUP));
        inputs.Add(Key(VK_RWIN, KEYEVENTF_KEYUP));

        foreach (char c in text)
        {
            inputs.Add(Unicode(c, 0));
            inputs.Add(Unicode(c, KEYEVENTF_KEYUP));
        }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(int vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } },
    };

    private static INPUT Unicode(char c, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | flags } },
    };

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
