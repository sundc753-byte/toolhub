using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ToolHub
{
    internal static class Program
    {
        private const string MutexName = @"Local\ToolHub.SingleInstance";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--smoke")
                return SmokeTest.Run();

            // 관리자 권한 전환 등으로 재실행될 때 이전 인스턴스가 끝나길 잠시 기다린다.
            Mutex mutex = new Mutex(false, MutexName);
            bool owned;
            try { owned = mutex.WaitOne(6000); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned)
            {
                MessageBox.Show("ToolHub가 이미 실행 중입니다. 작업 표시줄 알림 영역을 확인하세요.", "ToolHub",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HubContext());
            mutex.ReleaseMutex();
            return 0;
        }
    }

    internal sealed class HubContext : ApplicationContext
    {
        private const string AppName = "ToolHub";

        private readonly SettingsStore settings;
        private readonly HubHost host;
        private readonly List<ModuleBase> modules;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly System.Windows.Forms.Timer pollTimer;
        private ManagerForm manager;

        public HubContext()
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolHub");
            settings = new SettingsStore(Path.Combine(dataDir, "settings.ini"));
            host = new HubHost(settings, dataDir);
            host.Log("hub", "시작 (관리자=" + host.IsElevated + ")");

            menu = new ContextMenuStrip();
            menu.Opening += delegate { RebuildMenu(); };

            tray = new NotifyIcon();
            tray.Icon = CreateTrayIcon(host.IsElevated);
            tray.Text = AppName;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowManager(); };
            host.NotifyHandler = delegate(string title, string message)
            {
                tray.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
            };

            modules = ModuleLoader.Load(Path.Combine(host.AppDir, "modules"), host);

            // 사용 설정(기본 켜짐)된 모듈만 시작. 기존 단독 실행본 충돌은 한 번에 묻는다.
            List<ModuleBase> targets = new List<ModuleBase>();
            foreach (ModuleBase module in modules)
                if (settings.Get(module.Id, "enabled", "1") == "1") targets.Add(module);

            HashSet<ModuleBase> skipped = ResolveConflicts(targets);
            foreach (ModuleBase module in targets)
                if (!skipped.Contains(module)) TryStart(module);

            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 2000;
            pollTimer.Tick += delegate
            {
                foreach (ModuleBase module in modules) module.Poll();
            };
            pollTimer.Start();

            if (modules.Count == 0)
                tray.ShowBalloonTip(4000, AppName,
                    "modules 폴더에서 모듈을 찾지 못했습니다. build.ps1 로 빌드했는지 확인하세요.", ToolTipIcon.Warning);
        }

        // ----- 모듈 시작/중지 -----

        private bool TryStart(ModuleBase module)
        {
            try
            {
                module.Start();
                host.Log(module.Id, "시작됨");
                return true;
            }
            catch (Exception ex)
            {
                host.Log(module.Id, "시작 실패: " + ex.Message);
                tray.ShowBalloonTip(4000, module.DisplayName, "시작하지 못했습니다.\n" + ex.Message, ToolTipIcon.Error);
                return false;
            }
        }

        public void SetEnabled(ModuleBase module, bool enabled)
        {
            settings.Set(module.Id, "enabled", enabled ? "1" : "0");
            if (enabled)
            {
                List<ModuleBase> single = new List<ModuleBase>();
                single.Add(module);
                if (ResolveConflicts(single).Contains(module)) return;
                TryStart(module);
            }
            else
            {
                try { module.Stop(); host.Log(module.Id, "중지됨"); }
                catch (Exception ex) { host.Log(module.Id, "중지 실패: " + ex.Message); }
            }
        }

        // 같은 기능의 기존 단독 exe 가 떠 있으면 종료 여부를 묻는다. 반환값 = 시작하면 안 되는 모듈.
        private HashSet<ModuleBase> ResolveConflicts(List<ModuleBase> targets)
        {
            HashSet<ModuleBase> skipped = new HashSet<ModuleBase>();
            Dictionary<ModuleBase, List<Process>> found = new Dictionary<ModuleBase, List<Process>>();
            StringBuilder text = new StringBuilder();

            foreach (ModuleBase module in targets)
            {
                List<Process> list = new List<Process>();
                foreach (string name in module.ConflictProcessNames)
                {
                    try { list.AddRange(Process.GetProcessesByName(name)); }
                    catch { }
                }
                if (list.Count == 0) continue;
                found[module] = list;
                text.AppendLine("  • " + module.DisplayName + "  (" + list[0].ProcessName + ".exe)");
            }
            if (found.Count == 0) return skipped;

            DialogResult answer = MessageBox.Show(
                "ToolHub 모듈과 같은 기능의 기존 단독 프로그램이 실행 중입니다.\n\n" + text +
                "\n두 개가 동시에 동작하면 입력이 중복 처리됩니다.\n기존 프로그램을 종료하고 ToolHub로 통합 실행할까요?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            foreach (KeyValuePair<ModuleBase, List<Process>> pair in found)
            {
                bool ok = answer == DialogResult.Yes;
                if (ok)
                {
                    foreach (Process process in pair.Value)
                    {
                        try { process.Kill(); process.WaitForExit(3000); }
                        catch (Exception ex)
                        {
                            ok = false;
                            host.Log(pair.Key.Id, "기존 프로그램 종료 실패: " + ex.Message);
                        }
                    }
                }
                if (!ok)
                {
                    skipped.Add(pair.Key);
                    tray.ShowBalloonTip(4000, pair.Key.DisplayName,
                        "기존 프로그램이 남아 있어 이 모듈은 시작하지 않았습니다. (관리자 권한으로 실행 중이면 직접 종료해야 합니다)",
                        ToolTipIcon.Warning);
                }
            }
            return skipped;
        }

        // ----- 트레이 메뉴 -----

        private void RebuildMenu()
        {
            menu.Items.Clear();

            ToolStripMenuItem title = new ToolStripMenuItem(AppName + (host.IsElevated ? " (관리자)" : ""));
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());

            foreach (ModuleBase module in modules)
            {
                ModuleBase m = module;
                ToolStripMenuItem item = new ToolStripMenuItem(m.DisplayName);
                item.Checked = m.IsRunning;

                ToolStripMenuItem toggle = new ToolStripMenuItem("사용");
                toggle.Checked = m.IsRunning;
                toggle.Click += delegate { SetEnabled(m, !m.IsRunning); };
                item.DropDownItems.Add(toggle);

                IList<ToolStripItem> extra = null;
                try { extra = m.CreateMenuItems(); } catch { }
                if (extra != null && extra.Count > 0)
                {
                    item.DropDownItems.Add(new ToolStripSeparator());
                    foreach (ToolStripItem extraItem in extra) item.DropDownItems.Add(extraItem);
                }
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("관리 창 열기...", null, delegate { ShowManager(); });

            ToolStripMenuItem runKey = new ToolStripMenuItem("Windows 시작 시 실행");
            runKey.Checked = Autostart.IsRunKeyEnabled();
            runKey.Click += delegate { ToggleRunKey(); };
            menu.Items.Add(runKey);

            ToolStripMenuItem task = new ToolStripMenuItem("시작 시 관리자 권한으로 실행 (예약 작업)");
            task.Checked = Autostart.IsTaskEnabled();
            task.Click += delegate { ToggleTask(); };
            menu.Items.Add(task);

            menu.Items.Add("기존 단독 프로그램 자동 시작 항목 정리...", null, delegate { CleanupLegacyAutostart(); });

            menu.Items.Add(host.IsElevated ? "일반 권한으로 다시 시작" : "관리자 권한으로 다시 시작", null,
                delegate { Relaunch(!host.IsElevated); });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { ExitThread(); });
        }

        private void ToggleRunKey()
        {
            try
            {
                bool enable = !Autostart.IsRunKeyEnabled();
                if (enable && Autostart.IsTaskEnabled() && !Autostart.SetTask(false))
                    return;
                Autostart.SetRunKey(enable);
            }
            catch (Exception ex)
            {
                MessageBox.Show("자동 시작 설정을 바꾸지 못했습니다.\n\n" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ToggleTask()
        {
            bool enable = !Autostart.IsTaskEnabled();
            if (!Autostart.SetTask(enable))
            {
                MessageBox.Show("예약 작업을 변경하지 못했습니다. (UAC 확인을 취소했거나 권한이 없습니다)", AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (enable) { try { Autostart.SetRunKey(false); } catch { } }
        }

        // 각 모듈의 원래 단독 프로그램이 등록한 시작 프로그램 항목을 확인 후 제거한다.
        private void CleanupLegacyAutostart()
        {
            List<string> runValues = new List<string>();
            List<string> tasks = new List<string>();
            foreach (ModuleBase module in modules)
            {
                foreach (string name in module.LegacyRunValueNames)
                    if (Autostart.HasLegacyRunValue(name)) runValues.Add(name);
                foreach (string name in module.LegacyTaskNames)
                    if (Autostart.HasTask(name)) tasks.Add(name);
            }

            if (runValues.Count == 0 && tasks.Count == 0)
            {
                MessageBox.Show("정리할 기존 자동 시작 항목이 없습니다.", AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder text = new StringBuilder();
            foreach (string name in runValues) text.AppendLine("  • 시작 프로그램: " + name);
            foreach (string name in tasks) text.AppendLine("  • 예약 작업: " + name + " (UAC 확인 필요)");
            if (MessageBox.Show("다음 기존 항목을 제거할까요? (프로그램 파일은 삭제되지 않습니다)\n\n" + text,
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (string name in runValues) Autostart.RemoveLegacyRunValue(name);
            foreach (string name in tasks) Autostart.DeleteTaskElevated(name);
            host.Log("hub", "기존 자동 시작 항목 정리: " + string.Join(", ", runValues) +
                (tasks.Count > 0 ? " / 작업: " + string.Join(", ", tasks) : ""));
        }

        private void Relaunch(bool elevated)
        {
            try
            {
                if (elevated)
                {
                    ProcessStartInfo info = new ProcessStartInfo(Environment.ProcessPath);
                    info.Verb = "runas";
                    info.UseShellExecute = true;
                    Process.Start(info);
                }
                else
                {
                    // explorer 를 거치면 일반 권한으로 시작된다.
                    Process.Start("explorer.exe", "\"" + Environment.ProcessPath + "\"");
                }
                ExitThread();
            }
            catch (Win32Exception)
            {
                // UAC 취소: 그대로 유지
            }
        }

        // ----- 관리 창 -----

        private void ShowManager()
        {
            if (manager == null || manager.IsDisposed)
                manager = new ManagerForm(modules, host, SetEnabled);
            manager.RefreshList();
            manager.Show();
            manager.WindowState = FormWindowState.Normal;
            manager.Activate();
        }

        // ----- 종료 -----

        protected override void ExitThreadCore()
        {
            pollTimer.Stop();
            foreach (ModuleBase module in modules)
            {
                // 외부 exe 는 계속 실행 상태로 둔다(다음 실행 때 다시 인수). 내장 모듈만 정리.
                if (module is ExternalExeModule) continue;
                try { module.Stop(); } catch { }
            }
            if (manager != null && !manager.IsDisposed) manager.Dispose();
            tray.Visible = false;
            tray.Dispose();
            host.Log("hub", "종료");
            base.ExitThreadCore();
        }

        private static Icon CreateTrayIcon(bool elevated)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush back = new SolidBrush(elevated ? Color.FromArgb(192, 57, 43) : Color.FromArgb(37, 99, 235)))
                        g.FillRectangle(back, 1, 1, 30, 30);
                    using (SolidBrush dot = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(dot, 6, 6, 8, 8);
                        g.FillRectangle(dot, 18, 6, 8, 8);
                        g.FillRectangle(dot, 6, 18, 8, 8);
                        g.FillRectangle(dot, 18, 18, 8, 8);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }

    internal sealed class ManagerForm : Form
    {
        private readonly List<ModuleBase> modules;
        private readonly HubHost host;
        private readonly Action<ModuleBase, bool> setEnabled;
        private readonly ListView list;
        private readonly System.Windows.Forms.Timer timer;
        private bool refreshing;

        public ManagerForm(List<ModuleBase> modules, HubHost host, Action<ModuleBase, bool> setEnabled)
        {
            this.modules = modules;
            this.host = host;
            this.setEnabled = setEnabled;

            Text = "ToolHub - 모듈 관리";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 330);
            MinimumSize = new Size(620, 260);
            Font = SystemFonts.MessageBoxFont;

            list = new ListView();
            list.View = View.Details;
            list.CheckBoxes = true;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
            list.Dock = DockStyle.Fill;
            list.Columns.Add("모듈", 190);
            list.Columns.Add("상태", 80);
            list.Columns.Add("종류", 80);
            list.Columns.Add("설명", 380);
            list.ItemChecked += OnItemChecked;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 44;
            buttons.Padding = new Padding(8, 8, 8, 4);

            buttons.Controls.Add(MakeButton("선택 모듈 폴더", delegate { OpenSelectedFolder(); }));
            buttons.Controls.Add(MakeButton("모듈 폴더", delegate { OpenPath(Path.Combine(host.AppDir, "modules")); }));
            buttons.Controls.Add(MakeButton("로그 보기", delegate { OpenPath(host.LogPath); }));
            buttons.Controls.Add(MakeButton("설정 폴더", delegate { OpenPath(host.DataDir); }));

            Controls.Add(list);
            Controls.Add(buttons);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { if (Visible) RefreshList(); };
            timer.Start();
        }

        private static Button MakeButton(string text, EventHandler click)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Click += click;
            return button;
        }

        public void RefreshList()
        {
            refreshing = true;
            try
            {
                if (list.Items.Count != modules.Count)
                {
                    list.Items.Clear();
                    foreach (ModuleBase module in modules)
                    {
                        ListViewItem item = new ListViewItem(module.DisplayName);
                        item.SubItems.Add("");
                        item.SubItems.Add(module.Kind);
                        item.SubItems.Add(module.Description);
                        item.Tag = module;
                        list.Items.Add(item);
                    }
                }
                foreach (ListViewItem item in list.Items)
                {
                    ModuleBase module = (ModuleBase)item.Tag;
                    item.Checked = module.IsRunning;
                    item.SubItems[1].Text = module.IsRunning ? "실행 중" : "중지됨";
                }
            }
            finally { refreshing = false; }
        }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (refreshing) return;
            ModuleBase module = (ModuleBase)e.Item.Tag;
            if (e.Item.Checked == module.IsRunning) return;
            setEnabled(module, e.Item.Checked);
            RefreshList();
        }

        private void OpenSelectedFolder()
        {
            if (list.SelectedItems.Count == 0) return;
            ModuleBase module = (ModuleBase)list.SelectedItems[0].Tag;
            ExternalExeModule external = module as ExternalExeModule;
            if (external != null) OpenPath(Path.GetDirectoryName(external.ExePath));
            else OpenPath(Path.Combine(host.AppDir, "modules"));
        }

        private static void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            timer.Stop();
            base.OnFormClosing(e);
        }
    }
}
