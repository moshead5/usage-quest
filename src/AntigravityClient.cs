using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageQuest
{
    class AgResult
    {
        public bool Ok;
        public string Code;            // NOT_RUNNING | ERROR
        public string Message;
        public List<Meter> Meters = new List<Meter>();
        public string Email;
        public DateTime FetchedAt;
    }

    // Google Antigravity 사용량 읽기
    // Antigravity가 켜져 있을 때 PC 안에서 돌고 있는 Antigravity 언어 서버(language_server_windows_x64.exe)에
    // Antigravity 화면이 쓰는 것과 같은 요청(GetUserStatus / RetrieveUserQuotaSummary)을 보내 남은 양을 읽는다.
    // - 127.0.0.1(내 PC) 안에서만 통신, Google 계정 로그인 정보는 쓰지 않음
    // - Antigravity가 꺼져 있으면 읽을 수 없음
    // - 비공식 방식이라 Antigravity 업데이트로 바뀔 수 있음
    static class AntigravityClient
    {
        const string Svc = "/exa.language_server_pb.LanguageServerService/";
        static int cachedPort;
        static string cachedToken;
        static int cachedPid;

        // ---------- 언어 서버 찾기 ----------
        class Proc { public int Pid; public string Token; public int ExtPort; public string Name; }

        // 마지막 확인 과정 기록 (트레이 메뉴 "Antigravity 진단 정보 복사")
        public static string LastDiag = "아직 확인 전";
        static System.Text.StringBuilder diag;
        static void D(string line) { if (diag != null) diag.AppendLine(line); }

        static List<Proc> FindProcesses()
        {
            var list = new List<Proc>();
            if (!Native.IsWindows) return list;
            int total = 0, langServers = 0;
            try
            {
                // 이름이 바뀌어도 찾을 수 있게 --csrf_token 을 가진 프로세스를 모두 조회
                using (var q = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%csrf_token%'"))
                using (var rows = q.Get())
                {
                    foreach (System.Management.ManagementObject row in rows)
                    {
                        total++;
                        string cmd = Convert.ToString(row["CommandLine"]) ?? "";
                        string name = Convert.ToString(row["Name"]) ?? "";
                        int pid = Convert.ToInt32(row["ProcessId"]);
                        row.Dispose();
                        if (name.IndexOf("language_server", StringComparison.OrdinalIgnoreCase) >= 0) langServers++;
                        bool ag = IsAntigravity(cmd);
                        D("프로세스 " + name + " (PID " + pid + ") Antigravity=" + ag);
                        if (!ag) continue;
                        var m = Regex.Match(cmd, @"--csrf_token[=\s]+""?([a-zA-Z0-9\-]+)");
                        if (!m.Success) { D("  csrf_token 못 찾음"); continue; }
                        var ep = Regex.Match(cmd, @"--extension_server_port[=\s]+""?(\d+)");
                        list.Add(new Proc { Pid = pid, Token = m.Groups[1].Value, ExtPort = ep.Success ? int.Parse(ep.Groups[1].Value) : 0, Name = name });
                    }
                }
            }
            catch (Exception e) { D("프로세스 조회 실패: " + e.Message); }
            D("csrf_token 프로세스 " + total + "개, language_server " + langServers + "개, Antigravity " + list.Count + "개");
            return list;
        }

        static bool IsAntigravity(string cmd)
        {
            if (Regex.IsMatch(cmd, @"--app_data_dir[=\s]+""?antigravity\b", RegexOptions.IgnoreCase)) return true;
            return cmd.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------- 그 프로세스가 열어둔 포트 (명령 실행 없이 Windows API로) ----------
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        // (포트, IPv6 여부)
        static List<KeyValuePair<int, bool>> ListeningPorts(int pid)
        {
            var ports = new List<KeyValuePair<int, bool>>();
            if (!Native.IsWindows) return ports;
            const int TCP_TABLE_OWNER_PID_LISTENER = 3;
            foreach (var af in new[] { 2, 23 }) // AF_INET, AF_INET6
            {
                int rowSize = af == 2 ? 24 : 56, portOff = af == 2 ? 8 : 20, pidOff = af == 2 ? 20 : 52;
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (size <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) continue;
                    int n = Marshal.ReadInt32(buf);
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr row = IntPtr.Add(buf, 4 + i * rowSize);
                        if (Marshal.ReadInt32(row, pidOff) != pid) continue;
                        int raw = Marshal.ReadInt32(row, portOff);
                        int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                        if (port > 0) ports.Add(new KeyValuePair<int, bool>(port, af == 23));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return ports;
        }

        // ---------- 요청 ----------
        static Dictionary<string, object> Post(int port, string token, string method, bool https, int timeoutMs, bool v6 = false)
        {
            var url = (https ? "https" : "http") + "://" + (v6 ? "[::1]" : "127.0.0.1") + ":" + port + Svc + method;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Proxy = null; // 내 PC 안 통신은 프록시를 거치지 않음
            req.Headers["Connect-Protocol-Version"] = "1";
            req.Headers["X-Codeium-Csrf-Token"] = token;
            // 언어 서버는 자체 서명 인증서를 쓰므로 127.0.0.1 에 한해서만 인증서 검사를 건너뜀
            if (https) req.ServerCertificateValidationCallback = (a, b, c, d) => true;
            var body = Encoding.UTF8.GetBytes("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}");
            req.ContentLength = body.Length;
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                return Json.ParseObject(r.ReadToEnd());
        }

        static bool TryConnect(Proc p, out int port, out bool https, out bool v6)
        {
            port = 0; https = true; v6 = false;
            var cands = ListeningPorts(p.Pid);
            if (p.ExtPort > 0 && !cands.Exists(c => c.Key == p.ExtPort)) cands.Add(new KeyValuePair<int, bool>(p.ExtPort, false));
            D("PID " + p.Pid + " 포트: " + string.Join(", ", cands.Select(c => c.Key + (c.Value ? "(v6)" : "")).ToArray()));
            foreach (var c in cands)
            {
                foreach (var h in new[] { true, false })
                {
                    try
                    {
                        var j = Post(c.Key, p.Token, "GetUserStatus", h, 2500, c.Value);
                        if (j != null && Json.Obj(j, "userStatus") != null) { port = c.Key; https = h; v6 = c.Value; D("  " + c.Key + (h ? " https" : " http") + " 성공"); return true; }
                        D("  " + c.Key + (h ? " https" : " http") + " 응답에 userStatus 없음");
                    }
                    catch (WebException e)
                    {
                        var hr = e.Response as HttpWebResponse;
                        D("  " + c.Key + (h ? " https" : " http") + " 실패: " + (hr != null ? "HTTP " + (int)hr.StatusCode : e.Status.ToString()));
                    }
                    catch (Exception e) { D("  " + c.Key + (h ? " https" : " http") + " 실패: " + e.Message); }
                }
            }
            return false;
        }

        static bool cachedHttps = true, cachedV6;

        public static AgResult Fetch()
        {
            if (!Native.IsWindows) return new AgResult { Ok = false, Code = "NOT_RUNNING", Message = "Windows에서만 지원" };
            diag = new System.Text.StringBuilder();
            D(DateTime.Now.ToString("s") + " Usage Quest " + Updater.Short(Updater.Current));
            try
            {
                Dictionary<string, object> status = null;
                if (cachedPort > 0)
                {
                    try { status = Post(cachedPort, cachedToken, "GetUserStatus", cachedHttps, 4000, cachedV6); D("저장된 포트 " + cachedPort + " 사용"); }
                    catch (Exception e) { status = null; D("저장된 포트 " + cachedPort + " 실패: " + e.Message); }
                }
                if (status == null || Json.Obj(status, "userStatus") == null)
                {
                    cachedPort = 0;
                    var procs = FindProcesses();
                    if (procs.Count == 0) return Done(new AgResult { Ok = false, Code = "NOT_RUNNING", Message = "Antigravity가 꺼져 있음 (언어 서버를 못 찾음)" });
                    foreach (var p in procs)
                    {
                        int port; bool h, v6;
                        if (TryConnect(p, out port, out h, out v6)) { cachedPort = port; cachedToken = p.Token; cachedPid = p.Pid; cachedHttps = h; cachedV6 = v6; break; }
                    }
                    if (cachedPort == 0) return Done(new AgResult { Ok = false, Code = "ERROR", Message = "언어 서버에 연결하지 못함" });
                    status = Post(cachedPort, cachedToken, "GetUserStatus", cachedHttps, 4000, cachedV6);
                }
                Dictionary<string, object> summary = null;
                try { summary = Post(cachedPort, cachedToken, "RetrieveUserQuotaSummary", cachedHttps, 4000, cachedV6); D("RetrieveUserQuotaSummary 성공"); }
                catch (Exception e) { D("RetrieveUserQuotaSummary 실패: " + e.Message); }
                var r = Parse(status, summary);
                D("표시 항목 " + r.Meters.Count + "개");
                if (r.Meters.Count == 0) { r.Ok = false; r.Code = "ERROR"; r.Message = "응답에 사용량 정보가 없음"; }
                return Done(r);
            }
            catch (Exception e)
            {
                cachedPort = 0;
                return Done(new AgResult { Ok = false, Code = "ERROR", Message = e.Message });
            }
        }

        static AgResult Done(AgResult r)
        {
            D("결과: " + (r.Ok ? "성공" : r.Code + " " + r.Message));
            LastDiag = diag.ToString();
            return r;
        }

        // ---------- 해석 ----------
        static IEnumerable<Dictionary<string, object>> Arr(Dictionary<string, object> d, string key, string key2 = null)
        {
            object v = null;
            if (d != null && !d.TryGetValue(key, out v) && key2 != null) d.TryGetValue(key2, out v);
            var e = v as IEnumerable;
            if (e == null || v is string) yield break;
            foreach (var x in e) { var o = x as Dictionary<string, object>; if (o != null) yield return o; }
        }

        static string S2(Dictionary<string, object> d, string a, string b) { return Json.Str(d, a) ?? Json.Str(d, b); }
        static double? N2(Dictionary<string, object> d, string a, string b) { return Json.Num(d, a) ?? Json.Num(d, b); }
        static DateTime? D2(Dictionary<string, object> d, string a, string b) { return Json.Date(d, a) ?? Json.Date(d, b); }

        static string WindowName(string w)
        {
            if (w == null) return "한도";
            w = w.ToLowerInvariant();
            if (w == "5h" || w.Contains("five") || w.Contains("hour")) return "5시간";
            if (w.Contains("week") || w == "7d") return "주간";
            if (w.Contains("day")) return "일간";
            return w;
        }

        public static string ChipFor(string group)
        {
            var g = (group ?? "").ToLowerInvariant();
            if (g.Contains("gemini")) return "GEM";
            if (g.Contains("claude") || g.Contains("gpt")) return "3P";
            var w = Regex.Replace(group ?? "AG", "[^A-Za-z0-9]", "");
            return w.Length == 0 ? "AG" : w.Substring(0, Math.Min(3, w.Length)).ToUpperInvariant();
        }

        public static AgResult Parse(Dictionary<string, object> status, Dictionary<string, object> summary)
        {
            var r = new AgResult { Ok = true, FetchedAt = DateTime.UtcNow };
            var us = Json.Obj(status, "userStatus");
            r.Email = Json.Str(us, "email");

            // 1순위: 그룹별 5시간/주간 한도 (RetrieveUserQuotaSummary)
            var resp = Json.Obj(summary, "response");
            foreach (var g in Arr(resp, "groups"))
            {
                string gname = S2(g, "displayName", "display_name") ?? "Antigravity";
                var buckets = Arr(g, "buckets").ToList();
                // 5시간 먼저, 그 다음 주간
                buckets = buckets.OrderBy(b => WindowName(Json.Str(b, "window")) == "5시간" ? 0 : 1).ToList();
                foreach (var b in buckets)
                {
                    double? frac = N2(b, "remainingFraction", "remaining_fraction");
                    if (!frac.HasValue) continue;
                    string win = WindowName(Json.Str(b, "window"));
                    r.Meters.Add(new Meter
                    {
                        Key = "ag:" + ChipFor(gname), Chip = ChipFor(gname),
                        Title = gname + " · " + win,
                        Used = Math.Max(0, Math.Min(100, (1 - frac.Value) * 100)),
                        ResetsAt = D2(b, "resetTime", "reset_time"),
                        Main = win == "5시간",
                    });
                }
            }

            // 2순위: 모델별 한도 (같은 한도를 공유하는 모델은 하나로 묶음)
            if (r.Meters.Count == 0)
            {
                var cfg = Json.Obj(us, "cascadeModelConfigData");
                var seen = new Dictionary<string, Meter>();
                var counts = new Dictionary<string, int>();
                foreach (var m in Arr(cfg, "clientModelConfigs"))
                {
                    var q = Json.Obj(m, "quotaInfo") ?? Json.Obj(m, "quota_info");
                    double? frac = N2(q, "remainingFraction", "remaining_fraction");
                    if (!frac.HasValue) continue;
                    var reset = D2(q, "resetTime", "reset_time");
                    string label = Json.Str(m, "label") ?? "모델";
                    string key = Math.Round(frac.Value, 4) + "|" + reset;
                    if (seen.ContainsKey(key)) { counts[key]++; continue; }
                    counts[key] = 1;
                    seen[key] = new Meter
                    {
                        Key = "ag:" + ChipFor(label), Chip = ChipFor(label), Title = label,
                        Used = Math.Max(0, Math.Min(100, (1 - frac.Value) * 100)), ResetsAt = reset,
                    };
                }
                foreach (var kv in seen)
                {
                    if (counts[kv.Key] > 1) kv.Value.Title += " 외 " + (counts[kv.Key] - 1) + "개";
                    r.Meters.Add(kv.Value);
                }
                r.Meters = r.Meters.OrderByDescending(x => x.Used).ToList();
            }
            if (r.Meters.Count > 4) r.Meters = r.Meters.Take(4).ToList();
            return r;
        }

        // 화면 확인용 가짜 데이터
        public static AgResult Demo()
        {
            var now = DateTime.UtcNow;
            Func<double, double, string, Dictionary<string, object>> bucket = (frac, h, win) => new Dictionary<string, object>
            { { "window", win }, { "remainingFraction", frac }, { "resetTime", now.AddHours(h).ToString("o") } };
            var summary = new Dictionary<string, object>
            {
                { "response", new Dictionary<string, object> { { "groups", new object[] {
                    new Dictionary<string, object> { { "displayName", "Gemini Models" }, { "buckets", new object[] { bucket(0.84, 90, "weekly"), bucket(0.62, 3.2, "5h") } } },
                    new Dictionary<string, object> { { "displayName", "Claude and GPT models" }, { "buckets", new object[] { bucket(0.97, 120, "weekly") } } },
                } } } },
            };
            var status = new Dictionary<string, object> { { "userStatus", new Dictionary<string, object> { { "email", "demo@gmail.com" } } } };
            return Parse(status, summary);
        }
    }
}
