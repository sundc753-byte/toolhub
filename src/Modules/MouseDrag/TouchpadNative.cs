using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ToolHub.Modules.MouseDrag
{
class PadInfo
{
    public IntPtr Preparsed;
    public ushort[] Links;
    public ushort CountLink;
    public bool HasCount;
    public int MaxX = 1, MaxY = 1;
    public double PhysX = 1, PhysY = 1;
    public int Pending;
    public bool InFrame;
    public Dictionary<uint, Point> Cur = new Dictionary<uint, Point>();
}

class TouchpadWindow : NativeWindow
{
    const int WM_INPUT = 0x00FF;
    const double DragThreshold = 0.012;   // 패드 너비 대비 이 비율 이상 움직여야 드래그 시작 (세 손가락 탭과 구분)
    const int ReleaseDelayMs = 400;       // 손가락을 뗀 뒤 이 시간 안에 다시 대면 드래그 유지 (macOS 와 동일)

    public bool Enabled = true;
    public double Speed = 1.0;

    Dictionary<IntPtr, PadInfo> pads = new Dictionary<IntPtr, PadInfo>();
    Dictionary<uint, Point> prev = new Dictionary<uint, Point>();
    const int TransitionMs = 120;         // 3→2→1 로 손가락이 떼어지는 중간 상태를 허용하는 시간
    bool dragging, liftedAll;
    double accX, accY, remX, remY;
    Timer releaseTimer = new Timer();

    public TouchpadWindow()
    {
        CreateHandle(new CreateParams());
        var rid = new Native.RAWINPUTDEVICE[1];
        rid[0].usUsagePage = 0x0D;
        rid[0].usUsage = 0x05;
        rid[0].dwFlags = Native.RIDEV_INPUTSINK;
        rid[0].hwndTarget = Handle;
        if (!Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
            MessageBox.Show("터치패드 입력 등록에 실패했습니다.");

        releaseTimer.Interval = ReleaseDelayMs;
        releaseTimer.Tick += (s, e) => { releaseTimer.Stop(); ForceRelease(); };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            try { HandleInput(m.LParam); } catch { }
        }
        base.WndProc(ref m);
    }

    PadInfo GetPad(IntPtr device)
    {
        PadInfo p;
        if (pads.TryGetValue(device, out p)) return p;

        p = null;
        uint size = 0;
        Native.GetRawInputDeviceInfo(device, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size > 0)
        {
            IntPtr pre = Marshal.AllocHGlobal((int)size);
            if (Native.GetRawInputDeviceInfo(device, Native.RIDI_PREPARSEDDATA, pre, ref size) != unchecked((uint)-1))
            {
                Native.HIDP_CAPS caps;
                if (Native.HidP_GetCaps(pre, out caps) == Native.HIDP_STATUS_SUCCESS && caps.NumberInputValueCaps > 0)
                {
                    var vc = new Native.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
                    ushort n = caps.NumberInputValueCaps;
                    if (Native.HidP_GetValueCaps(0, vc, ref n, pre) == Native.HIDP_STATUS_SUCCESS)
                    {
                        var info = new PadInfo();
                        info.Preparsed = pre;
                        var links = new List<ushort>();
                        bool gotX = false, gotY = false;
                        for (int i = 0; i < n; i++)
                        {
                            var c = vc[i];
                            if (c.UsagePage == 0x0D && c.Usage == 0x51 && !links.Contains(c.LinkCollection)) links.Add(c.LinkCollection);
                            if (c.UsagePage == 0x0D && c.Usage == 0x54) { info.HasCount = true; info.CountLink = c.LinkCollection; }
                            if (c.UsagePage == 0x01 && c.Usage == 0x30 && !gotX)
                            {
                                gotX = true; info.MaxX = Math.Max(1, c.LogicalMax);
                                info.PhysX = c.PhysicalMax > 0 ? c.PhysicalMax : 0;
                            }
                            if (c.UsagePage == 0x01 && c.Usage == 0x31 && !gotY)
                            {
                                gotY = true; info.MaxY = Math.Max(1, c.LogicalMax);
                                info.PhysY = c.PhysicalMax > 0 ? c.PhysicalMax : 0;
                            }
                        }
                        links.Sort();
                        info.Links = links.ToArray();
                        if (info.Links.Length > 0 && info.HasCount && gotX && gotY) p = info;
                    }
                }
            }
            if (p == null) Marshal.FreeHGlobal(pre);
        }
        pads[device] = p;
        return p;
    }

    void HandleInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint hdrSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
        Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, hdrSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buf, ref size, hdrSize) != size) return;
            var header = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(Native.RAWINPUTHEADER));
            if (header.dwType != Native.RIM_TYPEHID) return;

            PadInfo pad = GetPad(header.hDevice);
            if (pad == null) return;

            int off = (int)hdrSize;
            uint sizeHid = (uint)Marshal.ReadInt32(buf, off);
            uint count = (uint)Marshal.ReadInt32(buf, off + 4);
            for (uint i = 0; i < count; i++)
            {
                byte[] report = new byte[sizeHid];
                Marshal.Copy(buf + off + 8 + (int)(i * sizeHid), report, 0, (int)sizeHid);
                ProcessReport(pad, report);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static bool GetValue(PadInfo pad, ushort page, ushort link, ushort usage, byte[] report, out uint value)
    {
        return Native.HidP_GetUsageValue(0, page, link, usage, out value, pad.Preparsed, report, (uint)report.Length)
               == Native.HIDP_STATUS_SUCCESS;
    }

    static bool IsTipDown(PadInfo pad, ushort link, byte[] report)
    {
        ushort[] usages = new ushort[16];
        uint len = (uint)usages.Length;
        if (Native.HidP_GetUsages(0, 0x0D, link, usages, ref len, pad.Preparsed, report, (uint)report.Length)
            != Native.HIDP_STATUS_SUCCESS) return false;
        for (int i = 0; i < len; i++) if (usages[i] == 0x42) return true;
        return false;
    }

    void ProcessReport(PadInfo pad, byte[] report)
    {
        uint cnt;
        if (GetValue(pad, 0x0D, pad.CountLink, 0x54, report, out cnt) && cnt > 0)
        {
            pad.Pending = (int)cnt;
            pad.InFrame = true;
            pad.Cur.Clear();
        }

        foreach (ushort link in pad.Links)
        {
            if (pad.Pending <= 0) break;
            uint cid, x, y;
            if (!GetValue(pad, 0x0D, link, 0x51, report, out cid)) continue;
            if (!GetValue(pad, 0x01, link, 0x30, report, out x)) continue;
            if (!GetValue(pad, 0x01, link, 0x31, report, out y)) continue;
            pad.Pending--;
            if (IsTipDown(pad, link, report)) pad.Cur[cid] = new Point((int)x, (int)y);
        }

        if (pad.InFrame && pad.Pending <= 0)
        {
            pad.InFrame = false;
            OnFrame(pad, pad.Cur);
            prev = new Dictionary<uint, Point>(pad.Cur);
        }
    }

    void OnFrame(PadInfo pad, Dictionary<uint, Point> cur)
    {
        if (!Enabled) return;

        if (cur.Count != 3)
        {
            if (!dragging) { accX = accY = 0; return; }

            if (cur.Count == 0)
            {
                // 손가락을 모두 뗌: 드래그 유지 대기 (다시 세 손가락을 대면 이어서 드래그)
                liftedAll = true;
                if (!releaseTimer.Enabled) { releaseTimer.Interval = ReleaseDelayMs; releaseTimer.Start(); }
            }
            else if (liftedAll)
            {
                // 모두 뗀 뒤 한/두 손가락이 닿음: 다른 조작이므로 즉시 드래그 종료
                ForceRelease();
            }
            else if (!releaseTimer.Enabled)
            {
                // 세 손가락을 떼는 중간 단계(3→2→1): 잠깐만 기다렸다가 계속 남아 있으면 종료
                releaseTimer.Interval = TransitionMs; releaseTimer.Start();
            }
            return;
        }

        releaseTimer.Stop();
        liftedAll = false;

        // 이전 프레임과 공통인 손가락들의 평균 이동량
        double dx = 0, dy = 0; int n = 0;
        foreach (var kv in cur)
        {
            Point o;
            if (prev.TryGetValue(kv.Key, out o)) { dx += kv.Value.X - o.X; dy += kv.Value.Y - o.Y; n++; }
        }
        if (n == 0) return;
        dx /= n; dy /= n;

        if (!dragging)
        {
            accX += dx; accY += dy;
            if (Math.Sqrt(accX * accX + accY * accY) < pad.MaxX * DragThreshold) return;
            dragging = true;
            remX = remY = 0;
            Native.MouseButton(true);
            dx = accX; dy = accY;
            accX = accY = 0;
        }

        // 화면 픽셀로 변환 (가로세로 물리 비율 유지)
        double screenW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        double sx = screenW * Speed / pad.MaxX;
        double ratioY = (pad.PhysX > 0 && pad.PhysY > 0) ? (pad.PhysY / pad.MaxY) / (pad.PhysX / pad.MaxX) : 1.0;
        double sy = sx * ratioY;

        remX += dx * sx; remY += dy * sy;
        int ix = (int)remX, iy = (int)remY;
        remX -= ix; remY -= iy;
        if (ix != 0 || iy != 0) Native.MoveCursorBy(ix, iy);
    }

    public void ForceRelease()
    {
        releaseTimer.Stop();
        if (dragging)
        {
            dragging = false;
            Native.MouseButton(false);
        }
        liftedAll = false;
        accX = accY = 0;
    }
}

static class Native
{
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RIM_TYPEHID = 2;
    public const uint HIDP_STATUS_SUCCESS = 0x00110000;
    public const int SM_CXSCREEN = 0;

    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort R1, R2, R3, R4, R5;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin, LogicalMax;
        public int PhysicalMin, PhysicalMax;
        public ushort Usage;      // (Range: UsageMin)
        public ushort UsageMaxOrReserved;
        public ushort StringIndex, Reserved2, DesignatorIndex, Reserved3, DataIndex, Reserved4;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint num, uint size);
    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll")]
    public static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);

    [DllImport("hid.dll")] public static extern uint HidP_GetCaps(IntPtr pre, out HIDP_CAPS caps);
    [DllImport("hid.dll")]
    public static extern uint HidP_GetValueCaps(int type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort len, IntPtr pre);
    [DllImport("hid.dll")]
    public static extern uint HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value,
        IntPtr pre, byte[] report, uint reportLen);
    [DllImport("hid.dll")]
    public static extern uint HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] usages, ref uint len,
        IntPtr pre, byte[] report, uint reportLen);

    public static void MouseButton(bool down)
    {
        var i = new INPUT[1];
        i[0].type = 0;
        i[0].mi.dwFlags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void MoveCursorBy(int dx, int dy)
    {
        POINT p;
        if (!GetCursorPos(out p)) return;
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int nx = Math.Max(vx, Math.Min(vx + vw - 1, p.X + dx));
        int ny = Math.Max(vy, Math.Min(vy + vh - 1, p.Y + dy));

        var i = new INPUT[1];
        i[0].type = 0;
        i[0].mi.dx = (int)(((long)(nx - vx) * 65535 + (vw - 1) / 2) / Math.Max(1, vw - 1));
        i[0].mi.dy = (int)(((long)(ny - vy) * 65535 + (vh - 1) / 2) / Math.Max(1, vh - 1));
        i[0].mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
}
