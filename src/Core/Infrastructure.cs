using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ToolHub
{
    // %APPDATA%\ToolHub\settings.ini  ([모듈ID] key=value)
    internal sealed class SettingsStore
    {
        private readonly string path;
        private readonly Dictionary<string, string> map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();

        public SettingsStore(string path)
        {
            this.path = path;
            Load();
        }

        public string Get(string section, string key, string defaultValue)
        {
            lock (sync)
            {
                string value;
                return map.TryGetValue(section + "|" + key, out value) ? value : defaultValue;
            }
        }

        public void Set(string section, string key, string value)
        {
            lock (sync)
            {
                map[section + "|" + key] = value;
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                string section = "";
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Substring(1, line.Length - 2);
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        map[section + "|" + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                SortedDictionary<string, SortedDictionary<string, string>> sections =
                    new SortedDictionary<string, SortedDictionary<string, string>>();
                foreach (KeyValuePair<string, string> pair in map)
                {
                    int bar = pair.Key.IndexOf('|');
                    string section = pair.Key.Substring(0, bar);
                    if (!sections.ContainsKey(section))
                        sections[section] = new SortedDictionary<string, string>();
                    sections[section][pair.Key.Substring(bar + 1)] = pair.Value;
                }
                StringBuilder text = new StringBuilder();
                foreach (KeyValuePair<string, SortedDictionary<string, string>> s in sections)
                {
                    text.AppendLine("[" + s.Key + "]");
                    foreach (KeyValuePair<string, string> kv in s.Value)
                        text.AppendLine(kv.Key + "=" + kv.Value);
                    text.AppendLine();
                }
                File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class HubHost : IModuleHost
    {
        private readonly SettingsStore settings;
        private readonly object logLock = new object();
        private readonly string logPath;

        public Action<string, string> NotifyHandler;

        public HubHost(SettingsStore settings, string dataDir)
        {
            this.settings = settings;
            DataDir = dataDir;
            AppDir = AppDomain.CurrentDomain.BaseDirectory;
            logPath = Path.Combine(dataDir, "ToolHub.log");
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                IsElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public string AppDir { get; private set; }
        public string DataDir { get; private set; }
        public bool IsElevated { get; private set; }
        public string LogPath { get { return logPath; } }

        public string GetSetting(string moduleId, string key, string defaultValue)
        {
            return settings.Get(moduleId, key, defaultValue);
        }

        public void SetSetting(string moduleId, string key, string value)
        {
            settings.Set(moduleId, key, value);
        }

        public void Log(string moduleId, string message)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                lock (logLock)
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + moduleId + "] " + message +
                        Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public void Notify(string title, string message)
        {
            Action<string, string> handler = NotifyHandler;
            if (handler != null) handler(title, message);
        }
    }

    internal static class ModuleLoader
    {
        // modules\<이름>\Module.<이름>.dll 에서 ModuleBase 를 상속한 클래스를 찾아 인스턴스를 만든다.
        public static List<ModuleBase> Load(string modulesDir, HubHost host)
        {
            List<ModuleBase> result = new List<ModuleBase>();
            if (!Directory.Exists(modulesDir)) return result;

            // 모듈 폴더 안의 보조 DLL(WinRT 등)도 찾을 수 있게 한다.
            AssemblyLoadContext.Default.Resolving += delegate(AssemblyLoadContext context, AssemblyName name)
            {
                foreach (string moduleDir in Directory.GetDirectories(modulesDir))
                {
                    string candidate = Path.Combine(moduleDir, name.Name + ".dll");
                    if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
                }
                return null;
            };

            string[] dirs = Directory.GetDirectories(modulesDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                string file = Path.Combine(dir, "Module." + Path.GetFileName(dir) + ".dll");
                if (!File.Exists(file)) continue;
                try
                {
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type.IsAbstract || !typeof(ModuleBase).IsAssignableFrom(type)) continue;
                        if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                        ModuleBase module = (ModuleBase)Activator.CreateInstance(type);
                        module.Initialize(host);
                        result.Add(module);
                        host.Log("hub", "모듈 로드: " + module.Id + " (" + Path.GetFileName(file) + ")");
                    }
                }
                catch (Exception ex)
                {
                    host.Log("hub", "모듈 로드 실패 " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            return result;
        }
    }

    internal static class Autostart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ToolHub";
        private const string TaskName = "ToolHub";

        public static bool IsRunKeyEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void SetRunKey(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                    key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
                else
                    key.DeleteValue(ValueName, false);
            }
        }

        public static bool RemoveLegacyRunValue(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null || key.GetValue(name) == null) return false;
                    key.DeleteValue(name, false);
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool HasLegacyRunValue(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                    return key != null && key.GetValue(name) != null;
            }
            catch { return false; }
        }

        public static bool HasTask(string taskName)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + taskName + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public static bool DeleteTaskElevated(string taskName)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", "/Delete /F /TN \"" + taskName + "\"");
                info.Verb = "runas";
                info.UseShellExecute = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Win32Exception) { return false; }
        }

        public static bool IsTaskEnabled()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // 관리자 권한 예약 작업은 만들고 지울 때 UAC 확인이 필요하다.
        public static bool SetTask(bool enabled)
        {
            string arguments = enabled
                ? "/Create /F /SC ONLOGON /RL HIGHEST /TN \"" + TaskName + "\" /TR \"\\\"" +
                  Environment.ProcessPath + "\\\"\""
                : "/Delete /F /TN \"" + TaskName + "\"";
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", arguments);
                info.Verb = "runas";
                info.UseShellExecute = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process process = Process.Start(info))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Win32Exception)
            {
                return false; // UAC 취소
            }
        }
    }
}
