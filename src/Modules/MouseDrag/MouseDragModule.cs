using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ToolHub.Modules.MouseDrag
{
    // 터치패드 세 손가락 드래그 (macOS 스타일)
    public sealed class MouseDragModule : ModuleBase
    {
        private static readonly double[] SpeedValues = { 0.6, 1.0, 1.6 };
        private static readonly string[] SpeedNames = { "느리게", "보통", "빠르게" };

        private TouchpadWindow window;

        public override string Id { get { return "mousedrag"; } }
        public override string DisplayName { get { return "세 손가락 드래그"; } }
        public override string Description { get { return "정밀 터치패드에서 세 손가락으로 끌어 드래그 (마우스 왼쪽 버튼 누른 채 이동)"; } }
        public override string[] ConflictProcessNames { get { return new string[] { "ThreeFingerDrag" }; } }
        public override string[] LegacyRunValueNames { get { return new string[] { "ThreeFingerDrag" }; } }

        protected override void OnStart()
        {
            window = new TouchpadWindow();
            window.Speed = SpeedValues[LoadSpeedIndex()];
        }

        protected override void OnStop()
        {
            if (window == null) return;
            window.ForceRelease();

            // 터치패드 Raw Input 등록 해제 (RIDEV_REMOVE)
            Native.RAWINPUTDEVICE[] rid = new Native.RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x0D;
            rid[0].usUsage = 0x05;
            rid[0].dwFlags = 0x00000001;
            rid[0].hwndTarget = IntPtr.Zero;
            Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));

            window.DestroyHandle();
            window = null;
        }

        private int LoadSpeedIndex()
        {
            string saved = Host.GetSetting(Id, "speed", null);
            int index;
            if (saved != null && int.TryParse(saved, out index) && index >= 0 && index < SpeedValues.Length)
                return index;

            // 기존 단독 프로그램이 레지스트리에 저장해 둔 값을 처음 한 번 이어받는다.
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\ThreeFingerDrag"))
                {
                    if (key != null)
                    {
                        int legacy = Convert.ToInt32(key.GetValue("Speed", 1));
                        if (legacy >= 0 && legacy < SpeedValues.Length) return legacy;
                    }
                }
            }
            catch { }
            return 1;
        }

        public override IList<ToolStripItem> CreateMenuItems()
        {
            List<ToolStripItem> items = new List<ToolStripItem>();
            ToolStripMenuItem speed = new ToolStripMenuItem("커서 속도");
            int current = LoadSpeedIndex();
            for (int i = 0; i < SpeedNames.Length; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(SpeedNames[i]);
                item.Checked = index == current;
                item.Click += delegate
                {
                    Host.SetSetting(Id, "speed", index.ToString());
                    if (window != null) window.Speed = SpeedValues[index];
                };
                speed.DropDownItems.Add(item);
            }
            items.Add(speed);
            return items;
        }
    }
}
