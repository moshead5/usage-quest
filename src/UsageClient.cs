using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace UsageQuest
{
    class Meter
    {
        public string Key, Chip, Title;
        public bool Main;
        public double Used;            // 사용한 % (0~100)
        public DateTime? ResetsAt;     // UTC
        public double Remaining { get { return 100 - Used; } }
    }

    class Account { public string Name, Email, Org; }

    class UsageResult
    {
        public bool Ok;
        public string Code, Message;
        public List<Meter> Meters = new List<Meter>();
        public string Plan;
        public Account Account;
        public DateTime FetchedAt;
        public bool? ExtraEnabled;
        public double? ExtraUtil;

        public Meter Get(string key)
        {
            foreach (var m in Meters) if (m.Key == key) return m;
            return null;
        }
        public static UsageResult Fail(string code, string msg) { return new UsageResult { Ok = false, Code = code, Message = msg }; }
    }

    // Claude Code /usage 와 같은 데이터 읽기
    // - 토큰: %USERPROFILE%\.claude\.credentials.json (CLAUDE_CONFIG_DIR 설정 시 그 경로) — 읽기만 함
    // - API: GET https://api.anthropic.com/api/oauth/usage (비공식, 예고 없이 바뀔 수 있음)
    static class UsageClient
    {
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
        const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

        // 의미가 확인된 항목만 표시 (iguana_necktie 같은 내부 코드명 항목은 제외)
        static readonly string[][] Known = {
            new[] { "five_hour", "HP", "현재 세션 · 5시간" },
            new[] { "seven_day", "STA", "주간 · 전체 모델" },
            new[] { "seven_day_opus", "OPUS", "주간 · Opus" },
            new[] { "seven_day_sonnet", "SNT", "주간 · Sonnet" },
        };

        static readonly Dictionary<string, Account> profileCache = new Dictionary<string, Account>();

        static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        static List<string> ConfigDirs()
        {
            var l = new List<string>();
            string env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(env)) l.Add(env);
            l.Add(Path.Combine(Home, ".claude"));
            return l;
        }

        static Dictionary<string, object> ReadCred(string customPath, out string source)
        {
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(customPath)) paths.Add(customPath);
            foreach (var d in ConfigDirs()) paths.Add(Path.Combine(d, ".credentials.json"));
            foreach (var p in paths)
            {
                try
                {
                    var j = Json.ParseObject(File.ReadAllText(p, Encoding.UTF8));
                    var o = Json.Obj(j, "claudeAiOauth");
                    if (o != null && Json.Str(o, "accessToken") != null) { source = p; return o; }
                }
                catch { }
            }
            source = null;
            return null;
        }

        static Account AccountFromLocal(string customPath)
        {
            var files = new List<string>();
            if (!string.IsNullOrEmpty(customPath))
            {
                try { files.Add(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(customPath)), ".claude.json")); } catch { }
            }
            string env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(env)) files.Add(Path.Combine(env, ".claude.json"));
            files.Add(Path.Combine(Home, ".claude.json"));
            foreach (var f in files)
            {
                try
                {
                    var a = Json.Obj(Json.ParseObject(File.ReadAllText(f, Encoding.UTF8)), "oauthAccount");
                    if (a == null) continue;
                    var acct = new Account { Name = Json.Str(a, "displayName"), Email = Json.Str(a, "emailAddress"), Org = Json.Str(a, "organizationName") };
                    if (acct.Name != null || acct.Email != null) return acct;
                }
                catch { }
            }
            return null;
        }

        static HttpWebRequest MakeRequest(string url, string token)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "usage-quest/2.0";
            req.Accept = "application/json";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req; // 시스템 프록시 설정을 기본으로 따름
        }

        static Account AccountFromApi(string token)
        {
            lock (profileCache) { Account c; if (profileCache.TryGetValue(token, out c)) return c; }
            try
            {
                var req = MakeRequest(ProfileUrl, token);
                req.Timeout = 10000;
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                {
                    var j = Json.ParseObject(r.ReadToEnd());
                    var a = Json.Obj(j, "account");
                    var o = Json.Obj(j, "organization");
                    var acct = new Account
                    {
                        Name = Json.Str(a, "display_name") ?? Json.Str(a, "full_name"),
                        Email = Json.Str(a, "email_address") ?? Json.Str(a, "email"),
                        Org = Json.Str(o, "name"),
                    };
                    if (acct.Name == null && acct.Email == null) return null;
                    lock (profileCache) profileCache[token] = acct;
                    return acct;
                }
            }
            catch { return null; }
        }

        // 토큰 갱신 확인용: 현재 저장된 토큰과 만료 시각
        public static bool TokenInfo(string customPath, out string token, out DateTime? expiresUtc)
        {
            string src;
            var cred = ReadCred(customPath, out src);
            token = cred != null ? Json.Str(cred, "accessToken") : null;
            double? exp = cred != null ? Json.Num(cred, "expiresAt") : null;
            expiresUtc = exp.HasValue ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(exp.Value) : (DateTime?)null;
            return cred != null;
        }

        static string PlanLabel(Dictionary<string, object> cred)
        {
            string t = Json.Str(cred, "subscriptionType");
            if (string.IsNullOrEmpty(t)) return "CLAUDE";
            t = t.ToUpperInvariant();
            string tier = Json.Str(cred, "rateLimitTier") ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(tier, @"(\d+)x", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? t + " " + m.Groups[1].Value + "x" : t;
        }

        public static UsageResult Fetch(string customPath)
        {
            string source;
            var cred = ReadCred(customPath, out source);
            if (cred == null) return UsageResult.Fail("NO_CREDENTIALS", "Claude Code 로그인 정보를 찾지 못했습니다.");

            string token = Json.Str(cred, "accessToken");
            double? exp = Json.Num(cred, "expiresAt");
            long nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            if (exp.HasValue && exp.Value < nowMs) return UsageResult.Fail("EXPIRED", "로그인 토큰이 만료되었습니다.");

            string body;
            try
            {
                var req = MakeRequest(UsageUrl, token);
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    body = r.ReadToEnd();
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr == null) return UsageResult.Fail("NETWORK", "서버에 연결하지 못했습니다. " + e.Message);
                int st = (int)hr.StatusCode;
                hr.Close();
                if (st == 401 || st == 403) return UsageResult.Fail("EXPIRED", "로그인 토큰이 거부되었습니다.");
                if (st == 429) return UsageResult.Fail("RATE_LIMIT", "너무 자주 확인했습니다.");
                return UsageResult.Fail("HTTP", "서버 오류 (HTTP " + st + ")");
            }
            catch (Exception e)
            {
                return UsageResult.Fail("NETWORK", "서버에 연결하지 못했습니다. " + e.Message);
            }

            var data = Json.ParseObject(body);
            if (data == null) return UsageResult.Fail("PARSE", "응답 형식을 읽지 못했습니다. API가 바뀌었을 수 있습니다.");

            var result = ParseData(data);
            result.Plan = PlanLabel(cred);
            result.Account = AccountFromLocal(customPath) ?? AccountFromApi(token);
            return result;
        }

        public static UsageResult ParseData(Dictionary<string, object> data)
        {
            var result = new UsageResult { Ok = true, FetchedAt = DateTime.UtcNow };
            foreach (var k in Known)
            {
                var v = Json.Obj(data, k[0]);
                double? u = Json.Num(v, "utilization");
                if (!u.HasValue) continue;
                result.Meters.Add(new Meter
                {
                    Key = k[0], Chip = k[1], Title = k[2], Main = k[0] == "five_hour",
                    Used = Math.Max(0, Math.Min(100, u.Value)),
                    ResetsAt = Json.Date(v, "resets_at"),
                });
            }
            var ex = Json.Obj(data, "extra_usage");
            if (ex != null)
            {
                result.ExtraEnabled = Json.Bool(ex, "is_enabled");
                result.ExtraUtil = Json.Num(ex, "utilization");
            }
            return result;
        }

        // 화면 확인용 가짜 데이터 (--demo=fresh|tired|ko|nologin|expired)
        public static UsageResult Demo(string variant)
        {
            if (variant == "nologin") return UsageResult.Fail("NO_CREDENTIALS", "Claude Code 로그인 정보를 찾지 못했습니다.");
            if (variant == "expired") return UsageResult.Fail("EXPIRED", "로그인 토큰이 만료되었습니다.");
            double[] p;
            if (variant == "tired") p = new double[] { 76, 63, 58, 40 };
            else if (variant == "ko") p = new double[] { 100, 92, 100, 71 };
            else p = new double[] { 18, 41, 12, 33 };
            var now = DateTime.UtcNow;
            Func<double, string> iso = h => now.AddHours(h).ToString("o");
            var d = new Dictionary<string, object>
            {
                { "five_hour", new Dictionary<string, object> { { "utilization", p[0] }, { "resets_at", iso(2.3) } } },
                { "seven_day", new Dictionary<string, object> { { "utilization", p[1] }, { "resets_at", iso(61) } } },
                { "seven_day_opus", new Dictionary<string, object> { { "utilization", p[2] }, { "resets_at", iso(61) } } },
                { "seven_day_sonnet", new Dictionary<string, object> { { "utilization", p[3] }, { "resets_at", iso(61) } } },
                { "iguana_necktie", new Dictionary<string, object> { { "utilization", 0.0 }, { "resets_at", iso(300) } } },
                { "extra_usage", new Dictionary<string, object> { { "is_enabled", false } } },
            };
            var r = ParseData(d);
            r.Plan = "MAX 5x";
            r.Account = new Account { Name = "데모 사용자", Email = "demo@example.com" };
            return r;
        }
    }
}
