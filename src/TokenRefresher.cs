using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace UsageQuest
{
    // 토큰이 만료되면 Claude Code(claude)를 창 없이 잠깐 실행해서 토큰을 갱신시킨다.
    // 토큰 갱신 자체는 Claude Code가 하고, 이 앱은 실행만 시킨다 (로그인 정보를 직접 고치지 않음).
    //  1단계: claude auth status  — 모델 호출 없음(사용량 0). 이걸로 갱신되면 끝
    //     (종료 코드 0 = 로그인됨, 1 = 로그인 안 됨 → 이 경우 /login 이 필요하므로 2단계를 하지 않음)
    //  2단계: claude -p "." --model haiku  — 1단계로 안 되면, 가장 작은 모델로 아주 짧은 요청 1번 (사용량 아주 조금)
    //     --safe-mode: 사용자 훅·MCP·플러그인·CLAUDE.md를 불러오지 않음 (인증은 정상 동작)
    //     --no-session-persistence: 대화 기록(/resume 목록)에 남기지 않음, --tools "": 도구 사용 안 함
    static class TokenRefresher
    {
        public class Result { public bool Ok; public string Note; }

        static string Env(string n) { return Environment.GetEnvironmentVariable(n) ?? ""; }

        // claude 실행 파일 찾기 (설치 방식마다 위치가 다름)
        static List<string[]> Candidates()
        {
            var l = new List<string[]>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var exes = new[]
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),          // 공식 설치 스크립트
                Path.Combine(local, "Programs", "claude", "claude.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe"),
            };
            foreach (var e in exes) if (File.Exists(e)) l.Add(new[] { e, "" });
            var npm = Path.Combine(appdata, "npm", "claude.cmd");             // npm 설치
            if (File.Exists(npm)) l.Add(new[] { Path.Combine(Env("SystemRoot"), "System32", "cmd.exe"), "/d /s /c \"\"" + npm + "\" " });
            // PATH에 있는 claude (cmd가 찾게 함)
            l.Add(new[] { Path.Combine(Env("SystemRoot"), "System32", "cmd.exe"), "/d /s /c \"claude " });
            return l;
        }

        static string WorkDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "UsageQuest-claude");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        // 숨김 실행. 반환: 종료 코드 (실행 실패 -1, 시간 초과 -2)
        static int RunHidden(string[] cand, string args, int timeoutMs)
        {
            string o;
            return RunHidden(cand, args, timeoutMs, out o);
        }

        static int RunHidden(string[] cand, string args, int timeoutMs, out string output)
        {
            output = "";
            var sb = new System.Text.StringBuilder();
            var psi = new ProcessStartInfo();
            psi.FileName = cand[0];
            psi.Arguments = cand[1] == "" ? args : cand[1] + args + "\"";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = WorkDir();
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    p.OutputDataReceived += (a, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (a, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { Process.Start(new ProcessStartInfo("taskkill", "/F /T /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }); } catch { }
                        try { p.Kill(); } catch { }
                        return -2;
                    }
                    p.WaitForExit();
                    lock (sb) output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        static bool Renewed(string customPath, string oldToken)
        {
            string tok; DateTime? exp;
            if (!UsageClient.TokenInfo(customPath, out tok, out exp) || tok == null) return false;
            bool fresh = !exp.HasValue || exp.Value > DateTime.UtcNow.AddMinutes(1);
            return fresh && tok != oldToken;
        }

        public static Result Run(string customPath, bool allowModelCall)
        {
            if (!Native.IsWindows) return new Result { Ok = false, Note = "Windows에서만 지원" };

            string oldToken; DateTime? oldExp;
            UsageClient.TokenInfo(customPath, out oldToken, out oldExp);

            // claude를 실행할 수 있는 후보 찾기 (auth status 로 확인)
            string[] found = null;
            foreach (var c in Candidates())
            {
                string outText;
                int code = RunHidden(c, "auth status", 30000, out outText);
                if (code == -1 || code == 9009) continue;      // 실행 실패 / cmd: 명령 없음
                found = c;
                if (Renewed(customPath, oldToken)) return new Result { Ok = true, Note = "auth status로 갱신됨" };
                // 종료 코드 1 + JSON의 loggedIn:false → 로그인 풀림 (예전 버전은 auth 명령이 없어 다른 이유로 1일 수 있음)
                if (code == 1 && outText.Replace(" ", "").Contains("\"loggedIn\":false")) return new Result { Ok = false, Note = "로그인이 풀려 있음 — 터미널에서 claude 실행 후 /login 필요" };
                break;
            }
            if (found == null) return new Result { Ok = false, Note = "claude 명령을 찾지 못함" };
            if (!allowModelCall) return new Result { Ok = false, Note = "auth status로는 갱신되지 않음" };

            // 2단계: 가장 작은 모델로 아주 짧은 요청
            int c2 = RunHidden(found, "-p \".\" --model haiku --max-turns 1 --safe-mode --no-session-persistence --tools \"\"", 90000);
            if (Renewed(customPath, oldToken)) return new Result { Ok = true, Note = "짧은 요청으로 갱신됨" };
            // 예전 버전 claude는 일부 옵션을 몰라서 바로 오류로 끝날 수 있음 → 최소 옵션으로 한 번 더
            if (c2 != -2)
            {
                c2 = RunHidden(found, "-p \".\" --model haiku --max-turns 1", 90000);
                if (Renewed(customPath, oldToken)) return new Result { Ok = true, Note = "짧은 요청으로 갱신됨" };
            }
            if (c2 == -2) return new Result { Ok = false, Note = "claude 응답 없음(시간 초과)" };
            return new Result { Ok = false, Note = "갱신 실패 — 다시 로그인이 필요할 수 있음" };
        }
    }
}
