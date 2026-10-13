using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ToolHub.Modules.PowerPoint
{
    internal sealed class MouseHook : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WmMouseWheel = 0x020A;
        private const int VkShift = 0x10;
        private const uint LlmhfInjected = 0x00000001;

        private readonly Func<int, bool> _onShiftWheel;
        private readonly NativeMethods.LowLevelMouseProc _hookProcedure;
        private IntPtr _hookHandle;
        private Thread _thread;
        private ApplicationContext _context;
        private int _remainder;
        private IntPtr _lastForeground;

        public MouseHook(Func<int, bool> onShiftWheel)
        {
            _onShiftWheel = onShiftWheel;
            _hookProcedure = HookCallback;
            _hookHandle = IntPtr.Zero;
        }

        public void Start()
        {
            Exception failure = null;
            using (var ready = new ManualResetEvent(false))
            {
                _thread = new Thread(delegate()
                {
                    try
                    {
                        _context = new ApplicationContext();
                        Install();
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { ready.Set(); }
                    if (failure == null) Application.Run(_context);
                });
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.WaitOne();
            }
            if (failure != null) throw failure;
            AppLog.Write("v2: dedicated input thread installed; SlidePane UI Automation enabled");
        }

        private void Install()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                return;
            }

            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule)
            {
                IntPtr moduleHandle = NativeMethods.GetModuleHandle(module == null ? null : module.ModuleName);
                _hookHandle = NativeMethods.SetWindowsHookEx(WhMouseLl, _hookProcedure, moduleHandle, 0);
            }

            if (_hookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam.ToInt32() == WmMouseWheel)
            {
                var data = (NativeMethods.MsllHookStruct)Marshal.PtrToStructure(
                    lParam,
                    typeof(NativeMethods.MsllHookStruct));
                bool isInjected = (data.Flags & LlmhfInjected) != 0;
                bool shiftDown = (NativeMethods.GetAsyncKeyState(VkShift) & 0x8000) != 0;

                bool otherModifier = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0 ||
                    (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0 ||
                    (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                    (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0;
                if (!isInjected && shiftDown && !otherModifier)
                {
                    short delta = unchecked((short)(data.MouseData >> 16));
                    IntPtr foreground = NativeMethods.GetForegroundWindow();
                    if (foreground != _lastForeground) { _remainder = 0; _lastForeground = foreground; }
                    int total = _remainder + delta;
                    int direction = -(total / 120);
                    try
                    {
                        if (delta != 0 && _onShiftWheel(direction))
                        {
                            _remainder = total % 120;
                            return new IntPtr(1);
                        }
                        _remainder = 0;
                    }
                    catch
                    {
                        // Never let an automation error escape from a low-level hook callback.
                    }
                }
                else _remainder = 0;
            }

            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookHandle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            if (_context != null) _context.ExitThread();
        }
    }

    internal static class AppLog
    {
        private static readonly object SyncRoot = new object();

        internal static void Write(string message)
        {
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PowerPointShiftWheel");
                System.IO.Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, "PowerPointShiftWheel.log");
                lock (SyncRoot)
                {
                    System.IO.File.AppendAllText(
                        path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message +
                        Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never interfere with scrolling.
            }
        }
    }

    internal static class NativeMethods
    {
        internal delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MsllHookStruct
        {
            internal Point Point;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelMouseProc callback,
            IntPtr instance,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            IntPtr parentWindow,
            EnumWindowsProc callback,
            IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        internal static string GetWindowClassName(IntPtr window)
        {
            var buffer = new StringBuilder(256);
            int length = GetClassName(window, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : String.Empty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CLSIDFromProgID(string programId, out Guid classId);

        [DllImport("oleaut32.dll")]
        internal static extern int GetActiveObject(ref Guid classId, IntPtr reserved, out IntPtr unknown);

        [DllImport("oleacc.dll")]
        internal static extern int AccessibleObjectFromWindow(
            IntPtr window,
            uint objectId,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object nativeObject);
    }
}
