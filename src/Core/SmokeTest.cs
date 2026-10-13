using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ToolHub
{
    // 점검용(--smoke): 모든 모듈을 충돌 확인 없이 잠깐 시작했다가 중지하고 결과를 %APPDATA%\ToolHub\smoke.txt 에 남긴다.
    internal static class SmokeTest
    {
        public static int Run()
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolHub");
            HubHost host = new HubHost(new SettingsStore(Path.Combine(dataDir, "settings.ini")), dataDir);
            StringBuilder report = new StringBuilder();
            int failures = 0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            List<ModuleBase> modules = ModuleLoader.Load(Path.Combine(host.AppDir, "modules"), host);
            report.AppendLine("modules=" + modules.Count);
            foreach (ModuleBase module in modules)
            {
                try
                {
                    module.Start();
                    report.AppendLine("START ok   " + module.Id + " running=" + module.IsRunning);
                }
                catch (Exception ex)
                {
                    failures++;
                    report.AppendLine("START FAIL " + module.Id + ": " + ex);
                }
            }

            // 메시지 루프를 잠깐 돌려 훅/스레드가 안정적으로 올라오는지 확인
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += delegate { timer.Stop(); Application.ExitThread(); };
            timer.Start();
            Application.Run();

            foreach (ModuleBase module in modules)
            {
                if (!module.IsRunning) continue;
                try
                {
                    string test = module.SelfTest();
                    if (test != null) report.AppendLine("TEST  " + module.Id + ": " + test);
                }
                catch (Exception ex)
                {
                    failures++;
                    report.AppendLine("TEST  FAIL " + module.Id + ": " + ex);
                }
            }

            foreach (ModuleBase module in modules)
            {
                try
                {
                    bool before = module.IsRunning;
                    module.Stop();
                    report.AppendLine("STOP  ok   " + module.Id + " (was running=" + before + ")");
                }
                catch (Exception ex)
                {
                    failures++;
                    report.AppendLine("STOP  FAIL " + module.Id + ": " + ex);
                }
            }

            report.AppendLine("failures=" + failures);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "smoke.txt"), report.ToString(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}
