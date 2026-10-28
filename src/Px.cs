using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;

namespace UsageQuest
{
    // 색, 글꼴, 픽셀 스타일 그리기 도우미
    static class Px
    {
        public static readonly Color Bg = C("#0d0b1a"), Bg2 = C("#16132b"), Panel = C("#1d1938"), PanelHi = C("#2a2550"),
            Ink = C("#f3ead7"), InkDim = C("#a49cc4"), InkFaint = C("#6c6591"), Line = C("#f3ead7"), LineDark = C("#07060f"),
            Accent = C("#e8875f"), AccentDk = C("#a8502f"), Good = C("#63d97f"), GoodDk = C("#2f8a49"),
            Warn = C("#f5c84b"), WarnDk = C("#a97f12"), Bad = C("#ef5a5a"), BadDk = C("#9a2a2a"),
            Sky = C("#69b4f0"), SkyDk = C("#2d6ea8"), Violet = C("#b48cf2"), VioletDk = C("#6b46b0"),
            SegOff = C("#2a2548"), Groove = C("#3a3466"), Dialog = C("#100d22");

        public static Color C(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
        }

        public static void MeterColors(string key, out Color c, out Color cd)
        {
            switch (key)
            {
                case "five_hour": c = Good; cd = GoodDk; break;
                case "seven_day": c = Sky; cd = SkyDk; break;
                case "seven_day_opus": c = Violet; cd = VioletDk; break;
                case "seven_day_sonnet": c = Warn; cd = WarnDk; break;
                default: c = Accent; cd = AccentDk; break;
            }
        }

        // 남은 양에 따른 막대 색 (위젯·작업표시줄 공통)
        public static void LevelColors(double remaining, Color baseC, Color baseCd, out Color c, out Color cd)
        {
            if (remaining < 25) { c = Bad; cd = BadDk; }
            else if (remaining < 50) { c = Warn; cd = WarnDk; }
            else { c = baseC; cd = baseCd; }
        }

        // ---------- 글꼴 ----------
        static PrivateFontCollection pfc;
        static readonly Dictionary<string, FontFamily> families = new Dictionary<string, FontFamily>();
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        public static void LoadFonts()
        {
            pfc = new PrivateFontCollection();
            string dir = Path.Combine(Path.GetTempPath(), "UsageQuest-fonts-2");
            try { Directory.CreateDirectory(dir); } catch { }
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "Galmuri11.ttf", "Galmuri11-Bold.ttf", "Galmuri9.ttf", "PressStart2P.ttf" })
            {
                string path = Path.Combine(dir, name);
                try
                {
                    if (!File.Exists(path))
                    {
                        using (var s = asm.GetManifestResourceStream(name))
                        {
                            if (s == null) continue;
                            using (var f = File.Create(path)) s.CopyTo(f);
                        }
                    }
                    pfc.AddFontFile(path);
                }
                catch { }
            }
            if (Native.IsWindows) foreach (var ff in pfc.Families) families[ff.Name] = ff;
        }

        // 이름 표기가 환경마다 달라서("Galmuri11" / "Galmuri11 Regular") 느슨하게 찾는다
        static FontFamily Fam(string name)
        {
            FontFamily f;
            if (families.TryGetValue(name, out f)) return f;
            if (families.TryGetValue(name + " Regular", out f)) return f;
            if (!Native.IsWindows)
            {
                // 리눅스(Mono) 미리보기: 시스템에 설치된 글꼴 사용
                try { return new FontFamily(name); } catch { }
            }
            return FontFamily.GenericSansSerif;
        }

        // kind: "g11" | "g11b" | "g9" | "ps"
        public static Font F(string kind, float px)
        {
            string key = kind + "|" + px;
            Font f;
            if (cache.TryGetValue(key, out f)) return f;
            FontFamily fam;
            FontStyle st = FontStyle.Regular;
            switch (kind)
            {
                case "g11b":
                    fam = Fam("Galmuri11 Bold");
                    if (fam == FontFamily.GenericSansSerif) { fam = Fam("Galmuri11"); st = FontStyle.Bold; }
                    break;
                case "g9": fam = Fam("Galmuri9"); break;
                case "ps": fam = Fam("Press Start 2P"); break;
                default: fam = Fam("Galmuri11"); break;
            }
            if (!fam.IsStyleAvailable(st)) st = FontStyle.Regular;
            f = new Font(fam, px, st, GraphicsUnit.Pixel);
            cache[key] = f;
            return f;
        }

        // ---------- 그리기 ----------
        public static readonly StringFormat Typo = MakeTypo();
        static StringFormat MakeTypo()
        {
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.None;
            return sf;
        }

        public static void Setup(Graphics g, float scale)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            bool crisp = Math.Abs(scale - Math.Round(scale)) < 0.01;
            g.TextRenderingHint = crisp ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;
        }

        public static SizeF Measure(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return new SizeF(0, f.Height);
            return g.MeasureString(s, f, PointF.Empty, Typo);
        }

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(s)) return;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y, Typo);
        }

        public static void TextShadow(Graphics g, string s, Font f, Color c, Color sh, float x, float y, float off)
        {
            Text(g, s, f, sh, x + off, y + off);
            Text(g, s, f, c, x, y);
        }

        // 오른쪽 정렬
        public static void TextR(Graphics g, string s, Font f, Color c, float right, float y)
        {
            Text(g, s, f, c, right - Measure(g, s, f).Width, y);
        }

        // 세로 가운데 정렬: 박스 [top, top+h] 안에 글자 높이 기준으로
        public static float MidY(Font f, float top, float h)
        {
            return top + (h - f.Size) / 2f - f.Size * 0.06f;
        }

        public static void Fill(Graphics g, Color c, float x, float y, float w, float h)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h);
        }

        // 픽셀 패널: 밝은 테두리 + 어두운 외곽 + 그림자 (CSS .panel 과 같은 모양)
        public static void PanelBox(Graphics g, RectangleF r, float px, Color fill)
        {
            Fill(g, Color.FromArgb(102, 0, 0, 0), r.X + 6 * px / 3, r.Y + 6 * px / 3, r.Width + px * 2, r.Height + px * 2);
            Fill(g, LineDark, r.X - px * 2, r.Y - px * 2, r.Width + px * 4, r.Height + px * 4);
            Fill(g, Line, r.X - px, r.Y - px, r.Width + px * 2, r.Height + px * 2);
            Fill(g, fill, r.X, r.Y, r.Width, r.Height);
        }

        // 칩(HP/STA 이름표)
        public static void Chip(Graphics g, string text, Font f, Color c, Color cd, RectangleF r, float sh)
        {
            Fill(g, cd, r.X + sh, r.Y + sh, r.Width, r.Height);
            Fill(g, c, r.X, r.Y, r.Width, r.Height);
            var sz = Measure(g, text, f);
            Text(g, text, f, LineDark, r.X + (r.Width - sz.Width) / 2f, MidY(f, r.Y, r.Height) + sh * 0.3f);
        }

        // 칸으로 나뉜 게이지 (남은 양만큼 켜짐)
        public static void SegBar(Graphics g, RectangleF r, double remaining, Color c, Color cd, int segs, float s, bool blinkTip, bool blinkOn)
        {
            Fill(g, LineDark, r.X, r.Y, r.Width, r.Height);
            float pad = Math.Max(1, (float)Math.Round(2 * s));
            float gap = Math.Max(1, (float)Math.Round(2 * s));
            var inner = new RectangleF(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2);
            using (var p = new Pen(Groove, Math.Max(1, (float)Math.Round(1 * s))))
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            int on = (int)Math.Ceiling(remaining / 100.0 * segs - 0.001);
            float w = (inner.Width - gap * (segs - 1)) / segs;
            for (int i = 0; i < segs; i++)
            {
                float x = inner.X + i * (w + gap);
                if (i < on)
                {
                    bool tip = i == on - 1;
                    if (blinkTip && tip && !blinkOn) { Fill(g, SegOff, x, inner.Y, w, inner.Height); continue; }
                    Fill(g, c, x, inner.Y, w, inner.Height);
                    float sh = Math.Max(1, (float)Math.Round(inner.Height * 0.22f));
                    Fill(g, cd, x, inner.Bottom - sh, w, sh);
                    Fill(g, Color.FromArgb(85, 255, 255, 255), x, inner.Y, w, Math.Max(1, (float)Math.Round(s)));
                }
                else Fill(g, SegOff, x, inner.Y, w, inner.Height);
            }
        }

        // 스캔라인 (브라운관 느낌)
        public static void Scanlines(Graphics g, Rectangle r, float s)
        {
            int step = Math.Max(3, (int)Math.Round(3 * s));
            using (var b = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                for (int y = r.Top; y < r.Bottom; y += step) g.FillRectangle(b, r.Left, y, r.Width, Math.Max(1, (int)Math.Round(s)));
        }
    }
}
