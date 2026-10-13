using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ToolHub
{
    // 허브가 모듈에게 제공하는 기능. 모듈은 이 인터페이스만 보고 동작한다.
    public interface IModuleHost
    {
        string AppDir { get; }
        string DataDir { get; }
        bool IsElevated { get; }
        string GetSetting(string moduleId, string key, string defaultValue);
        void SetSetting(string moduleId, string key, string value);
        void Log(string moduleId, string message);
        void Notify(string title, string message);
    }

    // 모든 모듈의 공통 부모. modules\*.dll 안에서 이 클래스를 상속한 public 클래스를 허브가 자동으로 찾는다.
    public abstract class ModuleBase
    {
        private bool running;

        public event EventHandler StateChanged;

        protected IModuleHost Host { get; private set; }

        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public abstract string Description { get; }

        public virtual string Kind { get { return "내장"; } }

        // 이 모듈과 같은 일을 하는 기존 단독 실행 프로그램의 프로세스 이름(통합 실행 시 충돌 방지).
        public virtual string[] ConflictProcessNames { get { return new string[0]; } }

        // 기존 단독 프로그램이 등록해 둔 시작 프로그램 항목(HKCU Run 값 이름 / 예약 작업 이름). 정리 메뉴에서 사용.
        public virtual string[] LegacyRunValueNames { get { return new string[0]; } }
        public virtual string[] LegacyTaskNames { get { return new string[0]; } }

        public bool IsRunning
        {
            get { return running; }
            protected set
            {
                if (running == value) return;
                running = value;
                EventHandler handler = StateChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public void Initialize(IModuleHost host)
        {
            Host = host;
        }

        public void Start()
        {
            if (running) return;
            OnStart();
            IsRunning = true;
        }

        public void Stop()
        {
            if (!running) return;
            try { OnStop(); }
            finally { IsRunning = false; }
        }

        // 허브가 2초마다 호출. 외부 프로세스 상태 동기화 등에 사용.
        public virtual void Poll() { }

        // 트레이 메뉴의 모듈 하위 메뉴에 추가될 항목(속도 설정 등).
        public virtual IList<ToolStripItem> CreateMenuItems()
        {
            return new List<ToolStripItem>();
        }

        // ToolHub.exe --smoke 에서 모듈이 실행 중일 때 호출되는 자체 점검. 결과 문장을 돌려주고, 없으면 null.
        public virtual string SelfTest()
        {
            return null;
        }

        protected abstract void OnStart();
        protected abstract void OnStop();
    }

    // 소스가 없는 기존 exe 를 모듈처럼 시작/중지/감시하기 위한 부모 클래스.
    // 실제 exe 는 modules\<Id>\<ExeFileName> 에 둔다.
    public abstract class ExternalExeModule : ModuleBase
    {
        protected abstract string ProcessName { get; }
        protected abstract string ExeFileName { get; }

        public override string Kind { get { return "외부 실행"; } }

        public string ExePath
        {
            get { return Path.Combine(Host.AppDir, "modules", Id, ExeFileName); }
        }

        public override void Poll()
        {
            IsRunning = FindProcesses().Length > 0;
        }

        protected override void OnStart()
        {
            if (FindProcesses().Length > 0) return; // 이미 실행 중이면 그대로 인수한다.

            string path = ExePath;
            if (!File.Exists(path))
                throw new FileNotFoundException("실행 파일을 찾을 수 없습니다.", path);

            if (Host.IsElevated)
            {
                // 허브가 관리자 권한이어도 외부 프로그램은 일반 권한으로 띄운다.
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            else
            {
                ProcessStartInfo info = new ProcessStartInfo(path);
                info.WorkingDirectory = Path.GetDirectoryName(path);
                info.UseShellExecute = false;
                Process.Start(info);
            }
        }

        protected override void OnStop()
        {
            foreach (Process process in FindProcesses())
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
        }

        private Process[] FindProcesses()
        {
            try { return Process.GetProcessesByName(ProcessName); }
            catch { return new Process[0]; }
        }
    }
}
