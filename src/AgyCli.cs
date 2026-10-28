using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageQuest
{
    // Antigravity CLI(agy) 연동 — 두 가지 방식 지원
    // [방식 1] 공식 statusLine: agy가 상태가 바뀔 때마다 "UsageQuest.exe --agy-statusline" 을 실행하면서 남은 사용량(quota)을 JSON으로 넘겨준다.
    //          그 값을 %APPDATA%\UsageQuest\agy-status.json 에 저장하고, 위젯은 그 파일을 읽는다.
    // [방식 2] 직접 호출(폴백): statusLine 저장값이 없으면 "agy -p /usage --output-format json" 을 직접 실행해 쿼타 JSON을 읽는다.
    //          로그인 정보는 전혀 쓰지 않는다 (agy가 직접 넘겨주는 값만 받음).
    static class AgyCli
    {
        public static string StatusFile { get { return Path.Combine(Settings.Dir, "agy-status.json"); } }
        static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        public static string SettingsPath { get { return Path.Combine(Home, ".gemini", "antigravity-cli", "settings.json"); } }

        public static string Command
        {
            get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --agy-statusline"; }
        }

        // ---------- agy 가 실행하는 쪽 (stdin JSON → 파일 저장 → 짧은 상태줄 출력) ----------
        public static int RunStatusline()
        {
            try
            {
                string input;
                using (var r = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)) input = r.ReadToEnd();
                var j = Json.ParseObject(input);
                if (j == null) return 0;
                var quota = Json.Obj(j, "quota");
                var save = new Dictionary<string, object>
                {
                    { "saved_at", DateTime.UtcNow.ToString("o") },
                    { "email", Json.Str(j, "email") },
                    { "plan_tier", Json.Str(j, "plan_tier") },
                    { "quota", quota ?? new Dictionary<string, object>() },
                };
                Directory.CreateDirectory(Settings.Dir);
                string tmp = StatusFile + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(save), Encoding.UTF8);
                try { if (File.Exists(StatusFile)) File.Delete(StatusFile); File.Move(tmp, StatusFile); }
                catch { try { File.Delete(tmp); } catch { } }

                // agy 상태줄 아래에 짧게 표시 (기본 상태줄과 함께 쌓아서 보여줌)
                var res = Parse(save, DateTime.UtcNow);
                if (res.Ok && res.Meters.Count > 0)
                {
                    var parts = res.Meters.Select(m => m.Chip + " " + (m.Title.Contains(" · ") ? m.Title.Substring(m.Title.LastIndexOf(" · ") + 3) : "") + " " + (int)Math.Round(m.Remaining) + "% 남음");
                    var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                    o.Write("Usage Quest · " + string.Join(" | ", parts.ToArray()));
                    o.Flush();
                }
            }
            catch { }
            return 0;
        }

        // ---------- 위젯 쪽 ----------
        static string WindowOf(string id)
        {
            id = (id ?? "").ToLowerInvariant();
            if (Regex.IsMatch(id, @"(5h|5-hour|five)")) return "5시간";
            if (id.Contains("week") || id.Contains("7d")) return "주간";
            if (id.Contains("day") || id.Contains("24h")) return "일간";
            if (id.Contains("month")) return "월간";
            return "한도";
        }

        static string GroupOf(string id)
        {
            id = (id ?? "").ToLowerInvariant();
            if (id.Contains("gemini")) return "Gemini";
            if (id.StartsWith("3p") || id.Contains("claude") || id.Contains("gpt") || id.Contains("third")) return "Claude·GPT";
            var w = Regex.Replace(id, @"[-_](5h|weekly|week|daily|day|monthly|7d|24h)$", "");
            return w.Length == 0 ? "Antigravity" : w;
        }

        public static AgResult Parse(Dictionary<string, object> saved, DateTime now)
        {
            var r = new AgResult { Ok = true, Source = "cli" };
            var at = Json.Date(saved, "saved_at");
            r.FetchedAt = at ?? now;
            r.Email = Json.Str(saved, "email");
            var quota = Json.Obj(saved, "quota");
            if (quota == null) { r.Ok = false; r.Code = "ERROR"; r.Message = "agy가 사용량을 넘겨주지 않음"; return r; }
            foreach (var kv in quota)
            {
                var b = kv.Value as Dictionary<string, object>;
                if (b == null || Json.Bool(b, "disabled") == true) continue;
                double? frac = Json.Num(b, "remaining_fraction") ?? Json.Num(b, "remainingFraction");
                if (!frac.HasValue) continue;
                DateTime? reset = Json.Date(b, "reset_time") ?? Json.Date(b, "resetTime");
                double? inSec = Json.Num(b, "reset_in_seconds");
                if (!reset.HasValue && inSec.HasValue) reset = r.FetchedAt.AddSeconds(inSec.Value);
                string group = GroupOf(kv.Key), win = WindowOf(kv.Key);
                string chip = AntigravityClient.ChipFor(group);
                r.Meters.Add(new Meter
                {
                    Key = "ag:" + chip, Chip = chip, Title = group + " · " + win,
                    Used = Math.Max(0, Math.Min(100, (1 - frac.Value) * 100)), ResetsAt = reset, Main = win == "5시간",
                });
            }
            r.Meters = r.Meters.OrderBy(m => m.Chip == "GEM" ? 0 : 1).ThenBy(m => m.Main ? 0 : 1).Take(4).ToList();
            if (r.Meters.Count == 0) { r.Ok = false; r.Code = "ERROR"; r.Message = "agy 사용량 정보가 비어 있음"; }
            return r;
        }

        // ---------- 방식 2: agy -p /usage --output-format json 직접 호출 ----------

        // agy CLI를 비대화형으로 실행하여 쿼타 JSON 데이터를 조회합니다.
        // agy 1.1.11+ 의 --output-format json 옵션을 사용합니다.
        public static AgResult FetchFromCli(int timeoutMs = 15000)
        {
            string agyPath = FindAgy();
            if (agyPath == null)
                return new AgResult { Ok = false, Code = "NOT_RUNNING", Message = "agy(Antigravity CLI)를 찾을 수 없음" };
            try
            {
                var psi = new ProcessStartInfo(agyPath, "-p /usage --output-format json")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                string stdout;
                int exitCode;
                using (var proc = Process.Start(psi))
                {
                    stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(timeoutMs);
                    exitCode = proc.ExitCode;
                }
                if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    return new AgResult { Ok = false, Code = "ERROR", Message = "agy /usage 응답 없음 (exitCode=" + exitCode + ")" };

                var data = Json.ParseObject(stdout);
                if (data == null)
                    return new AgResult { Ok = false, Code = "ERROR", Message = "agy /usage JSON 파싱 실패" };

                return ParseFromCliJson(data);
            }
            catch (Exception e)
            {
                return new AgResult { Ok = false, Code = "ERROR", Message = "agy 직접 호출 실패: " + e.Message };
            }
        }

        // agy -p /usage --output-format json 응답에서 미터를 파싱합니다.
        // 실제 응답 구조: { "command": { "data": { "groups": [ { "name": "...", "buckets": [ { "window": "5h"|"weekly", "remaining_fraction": 0.0~1.0, "reset_time": "..." } ] } ] } } }
        static AgResult ParseFromCliJson(Dictionary<string, object> data)
        {
            var r = new AgResult { Ok = true, Source = "cli", FetchedAt = DateTime.UtcNow };

            // command.data.groups 경로로 진입
            var command = Json.Obj(data, "command");
            var cmdData = Json.Obj(command, "data");

            object groupsObj;
            // command.data.groups 우선, 없으면 최상위 groups 폴백
            if (cmdData != null && cmdData.TryGetValue("groups", out groupsObj)) { }
            else data.TryGetValue("groups", out groupsObj);

            var groupsList = groupsObj as System.Collections.IEnumerable;
            if (groupsList == null)
                return new AgResult { Ok = false, Code = "ERROR", Message = "agy /usage 응답에 groups 없음" };

            foreach (var gRaw in groupsList)
            {
                var g = gRaw as Dictionary<string, object>;
                if (g == null) continue;
                // 실제 필드명: "name" (displayName 아님)
                string groupName = Json.Str(g, "name") ?? Json.Str(g, "displayName") ?? "Unknown";
                string chip = AntigravityClient.ChipFor(groupName);

                object bucketsObj;
                g.TryGetValue("buckets", out bucketsObj);
                var bucketsList = bucketsObj as System.Collections.IEnumerable;
                if (bucketsList == null) continue;

                foreach (var bRaw in bucketsList)
                {
                    var b = bRaw as Dictionary<string, object>;
                    if (b == null) continue;

                    // 실제 필드: remaining_fraction (직접 float, remaining 객체 없음)
                    double? frac = Json.Num(b, "remaining_fraction") ?? Json.Num(b, "remainingFraction");
                    if (!frac.HasValue) continue;

                    // window 필드로 시간 창 결정 ("5h", "weekly" 등)
                    string win = WindowOf(Json.Str(b, "window") ?? Json.Str(b, "id") ?? Json.Str(b, "name") ?? "");

                    DateTime? reset = Json.Date(b, "reset_time") ?? Json.Date(b, "resetTime");

                    r.Meters.Add(new Meter
                    {
                        Key = "ag:" + chip, Chip = chip,
                        Title = groupName + " · " + win,
                        Used = Math.Max(0, Math.Min(100, (1 - frac.Value) * 100)),
                        ResetsAt = reset,
                        Main = win == "5시간",
                    });
                }
            }

            r.Meters = r.Meters.OrderBy(m => m.Chip == "GEM" ? 0 : 1).ThenBy(m => m.Main ? 0 : 1).Take(4).ToList();
            if (r.Meters.Count == 0) { r.Ok = false; r.Code = "ERROR"; r.Message = "agy /usage 사용량 정보가 비어 있음"; }
            return r;
        }

        // PATH에서 agy 실행 파일 위치 찾기
        static string FindAgy()
        {
            // 1. PATH 탐색
            string[] exts = Native.IsWindows ? new[] { ".exe", ".cmd", "" } : new[] { "" };
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                foreach (var ext in exts)
                {
                    try
                    {
                        string full = Path.Combine(dir.Trim(), "agy" + ext);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            // 2. 알려진 설치 경로 (Windows)
            if (Native.IsWindows)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var candidates = new[]
                {
                    Path.Combine(local, "agy", "bin", "agy.exe"),          // 실제 설치 경로 (agy 공식)
                    Path.Combine(local, "agy", "agy.exe"),
                    Path.Combine(Home, ".gemini", "antigravity-cli", "agy.exe"),
                    Path.Combine(Home, ".gemini", "antigravity-cli", "agy.cmd"),
                    Path.Combine(local, "Programs", "antigravity-cli", "agy.exe"),
                };
                foreach (var c in candidates)
                    try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        // 저장된 CLI 값 읽기. 없으면 agy 직접 호출을 시도하고, 그것도 실패하면 null 반환
        public static AgResult ReadSaved()
        {
            // [방식 1] statusLine 저장 파일
            try
            {
                if (File.Exists(StatusFile))
                {
                    var j = Json.ParseObject(File.ReadAllText(StatusFile, Encoding.UTF8));
                    if (j != null)
                    {
                        var r = Parse(j, DateTime.UtcNow);
                        if (r != null && r.Ok) return r;
                    }
                }
            }
            catch { }

            // [방식 2] agy -p /usage --output-format json 직접 호출 (폴백)
            try
            {
                var cli = FetchFromCli();
                if (cli != null && cli.Ok) return cli;
            }
            catch { }

            return null;
        }

        // ---------- agy 설정에 상태줄 연결 ----------
        public static bool IsConnected()
        {
            try
            {
                var j = Json.ParseObject(File.ReadAllText(SettingsPath, Encoding.UTF8));
                var sl = Json.Obj(j, "statusLine");
                var cmd = Json.Str(sl, "command") ?? "";
                return cmd.IndexOf("--agy-statusline", StringComparison.OrdinalIgnoreCase) >= 0 && Json.Bool(sl, "enabled") != false;
            }
            catch { return false; }
        }

        public static string ExistingOtherCommand()
        {
            try
            {
                var j = Json.ParseObject(File.ReadAllText(SettingsPath, Encoding.UTF8));
                var cmd = Json.Str(Json.Obj(j, "statusLine"), "command");
                if (cmd != null && cmd.IndexOf("--agy-statusline", StringComparison.OrdinalIgnoreCase) < 0) return cmd;
            }
            catch { }
            return null;
        }

        // settings.json 의 다른 설정은 그대로 두고 statusLine 만 바꾼다 (기존 파일은 .bak 으로 백업)
        public static void Connect(bool connect)
        {
            Dictionary<string, object> j = null;
            if (File.Exists(SettingsPath))
            {
                string text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                j = Json.ParseObject(text);
                if (j == null) throw new Exception("settings.json 형식을 읽지 못함 (주석이 있으면 직접 수정이 필요)");
                File.Copy(SettingsPath, SettingsPath + ".usagequest.bak", true);
            }
            if (j == null) j = new Dictionary<string, object>();
            if (connect)
                j["statusLine"] = new Dictionary<string, object> { { "type", "command" }, { "command", Command }, { "stack_with_default", true } };
            else
                j.Remove("statusLine");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, Pretty(Json.Serialize(j)), new UTF8Encoding(false));
        }

        // 사람이 읽기 좋게 들여쓰기
        static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int ind = 0; bool str = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) str = !str;
                if (str) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[': sb.Append(c).Append('\n').Append(' ', ++ind * 4); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --ind * 4).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', ind * 4); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString() + "\n";
        }
    }
}
