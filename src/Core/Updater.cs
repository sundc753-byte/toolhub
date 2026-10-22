using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ToolHub
{
    /// <summary>GitHub 최신 릴리즈를 확인하고, 받아서 설치한 뒤 허브를 다시 시작한다.</summary>
    internal static class Updater
    {
        private const string LatestApi = "https://api.github.com/repos/sundc753-byte/toolhub/releases/latest";
        private const string AssetSuffix = "win-x64.zip";
        private const string VersionFile = "version.txt";

        private static bool busy;

        /// <summary>설치된 버전(릴리즈 태그). 릴리즈 zip 에 들어 있는 version.txt 를 읽는다. 없으면 빈 문자열.</summary>
        public static string CurrentVersion(string appDir)
        {
            try { return File.ReadAllText(Path.Combine(appDir, VersionFile)).Trim(); }
            catch { return ""; }
        }

        /// <summary>업데이트 확인 → 확인 대화상자 → 다운로드 → 종료 후 교체/재시작. 허브를 종료해야 하면 true.</summary>
        public static async Task<bool> RunAsync(HubHost host, string appName)
        {
            if (busy) return false;
            busy = true;
            try
            {
                string current = CurrentVersion(host.AppDir);
                string tag, assetUrl, notes;
                try
                {
                    using (HttpClient http = CreateClient())
                    {
                        string json = await http.GetStringAsync(LatestApi);
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            JsonElement root = doc.RootElement;
                            tag = root.GetProperty("tag_name").GetString() ?? "";
                            notes = root.TryGetProperty("body", out JsonElement b) ? (b.GetString() ?? "") : "";
                            assetUrl = null;
                            foreach (JsonElement a in root.GetProperty("assets").EnumerateArray())
                            {
                                string name = a.GetProperty("name").GetString() ?? "";
                                if (name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase))
                                {
                                    assetUrl = a.GetProperty("browser_download_url").GetString();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    host.Log("update", "확인 실패: " + ex.Message);
                    MessageBox.Show("최신 버전을 확인하지 못했습니다.\n\n" + ex.Message, appName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (assetUrl == null)
                {
                    MessageBox.Show("최신 릴리즈(" + tag + ")에서 설치 파일(" + AssetSuffix + ")을 찾지 못했습니다.", appName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (tag == current)
                {
                    MessageBox.Show("이미 최신 버전입니다. (" + tag + ")", appName,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                StringBuilder ask = new StringBuilder();
                ask.AppendLine("새 버전 " + tag + " 을(를) 설치할까요?");
                ask.AppendLine("현재 버전: " + (current.Length == 0 ? "알 수 없음" : current));
                if (notes.Trim().Length > 0)
                {
                    string n = notes.Trim();
                    ask.AppendLine().AppendLine(n.Length > 600 ? n.Substring(0, 600) + "…" : n);
                }
                ask.AppendLine().Append("설치하려면 ToolHub 가 종료되었다가 자동으로 다시 시작됩니다.");
                if (MessageBox.Show(ask.ToString(), appName, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                    != DialogResult.Yes)
                    return false;

                string work = Path.Combine(Path.GetTempPath(), "ToolHubUpdate");
                string zipPath = Path.Combine(work, "update.zip");
                string extract = Path.Combine(work, "files");
                try
                {
                    if (Directory.Exists(work)) Directory.Delete(work, true);
                    Directory.CreateDirectory(work);
                    using (HttpClient http = CreateClient())
                    using (HttpResponseMessage resp = await http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        using (Stream src = await resp.Content.ReadAsStreamAsync())
                        using (FileStream dst = File.Create(zipPath))
                            await src.CopyToAsync(dst);
                    }
                    ZipFile.ExtractToDirectory(zipPath, extract, true);
                    if (!File.Exists(Path.Combine(extract, "ToolHub.exe")))
                        throw new InvalidDataException("내려받은 파일에 ToolHub.exe 가 없습니다.");
                    // 릴리즈 zip 에 version.txt 가 빠져 있어도 다음 확인 때 최신으로 인식되도록 태그를 기록한다.
                    File.WriteAllText(Path.Combine(extract, VersionFile), tag);

                    StartApplyScript(work, extract, host.AppDir);
                    host.Log("update", current + " -> " + tag + " 설치를 위해 종료");
                    return true;
                }
                catch (Exception ex)
                {
                    host.Log("update", "설치 준비 실패: " + ex.Message);
                    MessageBox.Show("업데이트를 내려받거나 준비하지 못했습니다.\n\n" + ex.Message, appName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            finally { busy = false; }
        }

        private static HttpClient CreateClient()
        {
            HttpClient http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ToolHub-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        /// <summary>허브가 끝나길 기다렸다가 파일을 덮어쓰고 다시 실행하는 PowerShell 스크립트를 띄운다.</summary>
        private static void StartApplyScript(string work, string extract, string appDir)
        {
            string exe = Path.Combine(appDir, "ToolHub.exe");
            string script = Path.Combine(work, "apply.ps1");
            string log = Path.Combine(work, "apply.log");
            File.WriteAllText(script,
                "param([int]$HubPid,[string]$Src,[string]$Dst,[string]$Exe,[string]$Log)\r\n" +
                "$ErrorActionPreference='Continue'\r\n" +
                "try { Wait-Process -Id $HubPid -Timeout 30 -ErrorAction SilentlyContinue } catch {}\r\n" +
                "Start-Sleep -Milliseconds 500\r\n" +
                "robocopy $Src $Dst /E /R:10 /W:1 /NFL /NDL /NJH /NP | Out-File -FilePath $Log -Encoding utf8\r\n" +
                "Start-Process -FilePath $Exe\r\n",
                new UTF8Encoding(true));

            ProcessStartInfo info = new ProcessStartInfo("powershell.exe");
            info.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"" +
                " -HubPid " + Process.GetCurrentProcess().Id +
                " -Src \"" + extract + "\" -Dst \"" + appDir.TrimEnd('\\') + "\" -Exe \"" + exe + "\" -Log \"" + log + "\"";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            Process.Start(info);
        }
    }
}
