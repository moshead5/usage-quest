using System;
using System.Globalization;
using System.Linq;

namespace UsageQuest
{
    static class Fmt
    {
        static readonly CultureInfo Ko = new CultureInfo("ko-KR");

        public static string Left(DateTime? resetUtc)
        {
            if (!resetUtc.HasValue) return "";
            var ms = (resetUtc.Value - DateTime.UtcNow).TotalMilliseconds;
            if (ms <= 0) return "곧 회복";
            long s = (long)(ms / 1000);
            long d = s / 86400, h = (s % 86400) / 3600, m = (s % 3600) / 60, sec = s % 60;
            if (d > 0) return d + "일 " + h + "시간 후 회복";
            if (h > 0) return h + "시간 " + m + "분 후 회복";
            return m + "분 " + sec.ToString("00") + "초 후 회복";
        }

        public static string LeftShort(DateTime? resetUtc)
        {
            if (!resetUtc.HasValue) return "";
            var ms = (resetUtc.Value - DateTime.UtcNow).TotalMilliseconds;
            if (ms <= 0) return "곧";
            long m = (long)(ms / 60000);
            long d = m / 1440, h = (m % 1440) / 60;
            if (d > 0) return d + "d" + h + "h";
            if (h > 0) return h + "h" + (m % 60).ToString("00") + "m";
            return m + "m";
        }

        public static string ResetAt(DateTime? resetUtc)
        {
            if (!resetUtc.HasValue) return "";
            var t = resetUtc.Value.ToLocalTime();
            string time = t.ToString("tt h:mm", Ko);
            if (t.Date == DateTime.Now.Date) return "오늘 " + time;
            return t.ToString("M. d. (ddd) ", Ko) + time;
        }

        public static string Clock(DateTime utc) { return utc.ToLocalTime().ToString("tt h:mm:ss", Ko); }
    }

    class Mood
    {
        public string Face;   // fresh | mid | tired | ko
        public string Label;  // 쌩쌩 / 양호 ...
        public string Level;  // good | warn | bad
        public string Text;   // 대화창
    }

    static class Story
    {
        public static Mood For(UsageResult r)
        {
            var hp = r.Get("five_hour");
            var weekly = r.Meters.Where(x => x.Key != "five_hour").ToList();
            double hpLeft = hp != null ? hp.Remaining : 100;
            string hpWhen = hp != null && hp.ResetsAt.HasValue ? Fmt.Left(hp.ResetsAt).Replace(" 후 회복", "") : "";
            var weeklyOut = weekly.FirstOrDefault(x => x.Key == "seven_day" && x.Used >= 100);
            var modelOut = weekly.FirstOrDefault(x => x.Key != "seven_day" && x.Used >= 100);
            var weak = weekly.Where(x => x.Remaining < 20).OrderByDescending(x => x.Used).FirstOrDefault();

            if (weeklyOut != null)
                return new Mood { Face = "ko", Label = "탈진", Level = "bad",
                    Text = weeklyOut.Title + " 스태미나가 바닥났다… " + (weeklyOut.ResetsAt.HasValue ? Fmt.ResetAt(weeklyOut.ResetsAt) + "에 회복된다." : "회복을 기다리자.") };

            Mood m;
            int hl = (int)Math.Round(hpLeft);
            if (hpLeft <= 0) m = new Mood { Face = "ko", Label = "기절", Level = "bad", Text = "HP가 0이 되어 쓰러졌다! " + hpWhen + " 뒤에 부활한다." };
            else if (hpLeft < 25) m = new Mood { Face = "tired", Label = "빈사", Level = "bad", Text = "숨이 턱까지 찼다… HP " + hl + "% 남음. 회복까지 " + hpWhen + "." };
            else if (hpLeft < 50) m = new Mood { Face = "tired", Label = "지침", Level = "warn", Text = "아직 버틸 만하다. 큰 작업은 나눠서 진행하자. (HP " + hl + "%)" };
            else if (hpLeft < 80) m = new Mood { Face = "mid", Label = "양호", Level = "good", Text = "컨디션 양호! HP " + hl + "% 남았다. 계속 모험을 이어가자." };
            else m = new Mood { Face = "fresh", Label = "쌩쌩", Level = "good", Text = "체력이 넘친다! 오늘은 뭐든 만들 수 있을 것 같다." };

            if (modelOut != null) m.Text += " " + modelOut.Title + " 한도는 소진됨 — 다른 모델로 이어가자.";
            else if (weak != null) m.Text += " 단, " + weak.Title + " 스태미나가 " + (int)Math.Round(weak.Remaining) + "% 남았으니 아껴 쓰자.";
            return m;
        }
    }
}
