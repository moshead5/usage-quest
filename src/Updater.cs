using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace UsageQuest
{
    // GitHub Releases(moshead5/usage-quest)에서 새 버전 확인 → 설치파일 받아서 조용히 설치 → 앱 다시 실행
    static class Updater
    {
        public const string Owner = "moshead5";
        public const string Repo = "usage-quest";
        static string LatestUrl { get { return "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest"; } }
        public static string ReleasesPage { get { return "https://github.com/" + Owner + "/" + Repo + "/releases"; } }

        public class Info
        {
            public Version Version;
            public string Tag, Notes, Url, Sha256;
            public long Size;
        }

        public static Version Current
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static string Short(Version v) { return v == null ? "?" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        static Version Norm(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        // 새 버전이 있으면 Info, 없으면 null. 실패하면 예외
        public static Info Check()
        {
            var req = (HttpWebRequest)WebRequest.Create(LatestUrl);
            req.UserAgent = "usage-quest-updater";
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            string body;
            try
            {
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd();
            }
            catch (WebException e)
            {
                var hr = e.Response as HttpWebResponse;
                if (hr != null && (int)hr.StatusCode == 404) return null; // 아직 릴리스 없음
                throw;
            }
            var j = Json.ParseObject(body);
            if (j == null) throw new Exception("릴리스 정보를 읽지 못함");
            if (Json.Bool(j, "draft") == true || Json.Bool(j, "prerelease") == true) return null;
            string tag = Json.Str(j, "tag_name") ?? "";
            var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            var ver = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
            if (ver <= Norm(Current)) return null;

            object assetsObj;
            j.TryGetValue("assets", out assetsObj);
            var assets = assetsObj as IEnumerable;
            if (assets == null) return null;
            foreach (var a in assets)
            {
                var d = a as Dictionary<string, object>;
                string name = Json.Str(d, "name") ?? "";
                if (!Regex.IsMatch(name, @"^UsageQuest-Setup-.*\.exe$", RegexOptions.IgnoreCase)) continue;
                string notes = Json.Str(j, "body") ?? "";
                // 릴리스 설명에 "sha256: <64자리>" 가 있으면 받은 파일을 검증한다
                var sm = Regex.Match(notes, @"sha256[:\s]+([0-9a-fA-F]{64})", RegexOptions.IgnoreCase);
                double? size = Json.Num(d, "size");
                return new Info
                {
                    Version = ver, Tag = tag,
                    Notes = Regex.Replace(notes, @"(?im)^.*sha256.*$", "").Trim(),
                    Url = Json.Str(d, "browser_download_url"),
                    Size = size.HasValue ? (long)size.Value : 0,
                    Sha256 = sm.Success ? sm.Groups[1].Value.ToLowerInvariant() : null,
                };
            }
            return null;
        }

        // 설치파일 다운로드 (progress: 0~100). 반환: 받은 파일 경로
        public static string Download(Info info, Action<int> progress)
        {
            if (info.Url == null || !info.Url.StartsWith("https://github.com/" + Owner + "/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new Exception("다운로드 주소가 올바르지 않음");
            string dir = Path.Combine(Path.GetTempPath(), "UsageQuest-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "UsageQuest-Setup-" + Short(info.Version) + ".exe");
            if (File.Exists(path)) File.Delete(path);

            var req = (HttpWebRequest)WebRequest.Create(info.Url);
            req.UserAgent = "usage-quest-updater";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.AllowAutoRedirect = true;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var s = res.GetResponseStream())
            using (var f = File.Create(path))
            {
                long total = res.ContentLength > 0 ? res.ContentLength : info.Size;
                long got = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n);
                    got += n;
                    if (total > 0)
                    {
                        int p = (int)(got * 100 / total);
                        if (p != last) { last = p; if (progress != null) progress(p); }
                    }
                }
            }

            var fi = new FileInfo(path);
            if (info.Size > 0 && fi.Length != info.Size) { File.Delete(path); throw new Exception("받은 파일 크기가 다름"); }
            if (info.Sha256 != null)
            {
                string hash;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (hash != info.Sha256) { File.Delete(path); throw new Exception("파일 검증(SHA-256) 실패"); }
            }
            return path;
        }

        // 조용히 설치하고 끝나면 앱을 다시 실행 (/S: 화면 없이, /RUN: 설치 후 실행)
        public static void RunInstaller(string path)
        {
            Process.Start(new ProcessStartInfo(path, "/S /RUN") { UseShellExecute = true });
        }
    }
}
