using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ToolHub.Modules.Excel
{
    internal sealed class MouseWheelHook : IDisposable
    {
        private const int WhMouseLl = 14;
        private const int WmMouseWheel = 0x020A;
        private const int VkShift = 0x10;
        private const uint InputMouse = 0;
        private const uint MouseEventHorizontalWheel = 0x01000;
        private const uint ObjectIdNativeObjectModel = 0xFFFFFFF0;

        private readonly NativeMethods.LowLevelMouseProc callback;
        private readonly Control dispatcher;
        private IntPtr hookHandle;
        private bool disposed;

        public bool Enabled { get; set; }

        public MouseWheelHook()
        {
            Enabled = true;
            callback = HookCallback;
            dispatcher = new Control();
            // Force creation on the STA UI thread before the hook can receive input.
            IntPtr dispatcherHandle = dispatcher.Handle;
        }

        public void Start()
        {
            if (hookHandle != IntPtr.Zero)
                return;

            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
            {
                IntPtr moduleHandle = NativeMethods.GetModuleHandle(module == null ? null : module.ModuleName);
                hookHandle = NativeMethods.SetWindowsHookEx(WhMouseLl, callback, moduleHandle, 0);
            }

            if (hookHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && wParam.ToInt32() == WmMouseWheel && Enabled
                && IsShiftPressed() && IsExcelForeground())
            {
                NativeMethods.MsllHookStruct data =
                    (NativeMethods.MsllHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MsllHookStruct));
                int verticalDelta = unchecked((short)((data.MouseData >> 16) & 0xffff));
                int horizontalDelta = -verticalDelta;

                // Excel's custom worksheet window ignores ordinary horizontal-wheel and
                // WM_HSCROLL messages on some Office builds. Call the native Excel window's
                // SmallScroll method, obtained from the focused EXCEL7 child window.
                // A low-level hook must return immediately. Queue Excel COM work onto
                // the STA message loop to avoid input-thread/Excel COM deadlocks.
                if (QueueExcelScroll(horizontalDelta))
                    return new IntPtr(1);

                // Keep a generic horizontal-wheel fallback for unusual Excel window layouts.
                NativeMethods.Input input = new NativeMethods.Input();
                input.Type = InputMouse;
                input.Union.Mouse = new NativeMethods.MouseInput();
                input.Union.Mouse.MouseData = unchecked((uint)horizontalDelta);
                input.Union.Mouse.Flags = MouseEventHorizontalWheel;

                uint sent = NativeMethods.SendInput(1, ref input,
                    Marshal.SizeOf(typeof(NativeMethods.Input)));
                if (sent == 1)
                {
                    // Do not also perform the original vertical scroll.
                    return new IntPtr(1);
                }
            }

            return NativeMethods.CallNextHookEx(hookHandle, code, wParam, lParam);
        }

        private bool QueueExcelScroll(int horizontalDelta)
        {
            try
            {
                dispatcher.BeginInvoke((MethodInvoker)delegate
                {
                    TryScrollFocusedExcel(horizontalDelta);
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryScrollFocusedExcel(int horizontalDelta)
        {
            IntPtr mainWindow = NativeMethods.GetForegroundWindow();
            IntPtr worksheetWindow = IntPtr.Zero;

            NativeMethods.EnumChildWindows(mainWindow, delegate(IntPtr window, IntPtr data)
            {
                StringBuilder className = new StringBuilder(64);
                NativeMethods.GetClassName(window, className, className.Capacity);
                if (className.ToString() == "EXCEL7" && NativeMethods.IsWindowVisible(window))
                {
                    worksheetWindow = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (worksheetWindow == IntPtr.Zero)
                return false;

            object nativeWindow = null;
            try
            {
                Guid dispatchInterface = new Guid("00020400-0000-0000-C000-000000000046");
                int result = NativeMethods.AccessibleObjectFromWindow(worksheetWindow,
                    ObjectIdNativeObjectModel, ref dispatchInterface, out nativeWindow);
                if (result != 0 || nativeWindow == null)
                    return false;

                int steps = Math.Max(1, Math.Abs(horizontalDelta) / 120);
                dynamic excelWindow = nativeWindow;
                if (horizontalDelta > 0)
                    excelWindow.SmallScroll(Type.Missing, Type.Missing, steps, Type.Missing);
                else
                    excelWindow.SmallScroll(Type.Missing, Type.Missing, Type.Missing, steps);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (nativeWindow != null && Marshal.IsComObject(nativeWindow))
                    Marshal.ReleaseComObject(nativeWindow);
            }
        }

        private static bool IsShiftPressed()
        {
            return (NativeMethods.GetAsyncKeyState(VkShift) & 0x8000) != 0;
        }

        private static bool IsExcelForeground()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;

            uint processId;
            NativeMethods.GetWindowThreadProcessId(foreground, out processId);
            if (processId == 0)
                return false;

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                    return process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            if (hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
            dispatcher.Dispose();
        }
    }

    internal static class NativeMethods
    {
        internal delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MsllHookStruct
        {
            public NativePoint Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            public uint Type;
            public InputUnion Union;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("oleacc.dll")]
        internal static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object nativeObject);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint inputCount, ref Input input, int inputSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);
    }
}
