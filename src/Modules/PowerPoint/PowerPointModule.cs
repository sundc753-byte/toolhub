using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ToolHub.Modules.PowerPoint
{
    // PowerPoint 편집 화면에서 Shift + 마우스 휠 = 가로 스크롤
    public sealed class PowerPointModule : ModuleBase
    {
        private MouseHook hook;
        private SlideScrollController scroller;
        private int step = 3;

        public override string Id { get { return "powerpoint"; } }
        public override string DisplayName { get { return "파워포인트 가로 스크롤"; } }
        public override string Description { get { return "PowerPoint 편집 화면에서 Shift + 마우스 휠로 가로 스크롤"; } }
        public override string[] ConflictProcessNames { get { return new string[] { "PowerPointShiftWheel" }; } }
        public override string[] LegacyRunValueNames { get { return new string[] { "PowerPointShiftWheel", "PowerPoint Shift Wheel" }; } }
        public override string[] LegacyTaskNames { get { return new string[] { "PowerPointShiftWheel" }; } }

        protected override void OnStart()
        {
            int parsed;
            step = int.TryParse(Host.GetSetting(Id, "step", "3"), out parsed) ? parsed : 3;

            scroller = new SlideScrollController();
            hook = new MouseHook(HandleShiftWheel);
            try
            {
                hook.Start();
            }
            catch
            {
                scroller.Dispose();
                scroller = null;
                hook = null;
                throw;
            }
        }

        protected override void OnStop()
        {
            if (hook != null) hook.Dispose();
            if (scroller != null) scroller.Dispose();
            hook = null;
            scroller = null;
        }

        private bool HandleShiftWheel(int direction)
        {
            SlideScrollController current = scroller;
            return current != null && current.QueueScroll(direction, step);
        }

        public override IList<ToolStripItem> CreateMenuItems()
        {
            List<ToolStripItem> items = new List<ToolStripItem>();
            ToolStripMenuItem speed = new ToolStripMenuItem("스크롤 속도");
            int[] values = new int[] { 1, 3, 5 };
            string[] names = new string[] { "느리게 (1)", "보통 (3)", "빠르게 (5)" };
            for (int i = 0; i < values.Length; i++)
            {
                int value = values[i];
                ToolStripMenuItem item = new ToolStripMenuItem(names[i]);
                item.Checked = step == value;
                item.Click += delegate
                {
                    step = value;
                    Host.SetSetting(Id, "step", value.ToString());
                };
                speed.DropDownItems.Add(item);
            }
            items.Add(speed);
            return items;
        }
    }
}
