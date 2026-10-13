using System;

namespace ToolHub.Modules.Excel
{
    // Excel 이 활성 창일 때 Shift + 마우스 휠 = 가로 스크롤
    public sealed class ExcelModule : ModuleBase
    {
        private MouseWheelHook hook;

        public override string Id { get { return "excel"; } }
        public override string DisplayName { get { return "엑셀 가로 스크롤"; } }
        public override string Description { get { return "Excel에서 Shift + 마우스 휠로 가로 스크롤 (휠 아래 = 오른쪽)"; } }

        public override string[] LegacyRunValueNames { get { return new string[] { "Excel Shift Wheel" }; } }

        public override string[] ConflictProcessNames
        {
            get
            {
                return new string[] { "ExcelShiftWheel", "ExcelShiftWheel-v3", "ExcelShiftWheel-v4",
                    "ExcelShiftWheel-final", "ExcelShiftWheel-fixed" };
            }
        }

        protected override void OnStart()
        {
            MouseWheelHook created = new MouseWheelHook();
            try
            {
                created.Start();
            }
            catch
            {
                created.Dispose();
                throw;
            }
            hook = created;
        }

        protected override void OnStop()
        {
            if (hook != null) hook.Dispose();
            hook = null;
        }
    }
}
