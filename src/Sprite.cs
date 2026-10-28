using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UsageQuest
{
    // 16x16 픽셀 캐릭터 (오리지널 디자인). 상태별로 눈/입이 바뀐다.
    static class Sprite
    {
        static readonly Dictionary<char, Color> Pal = new Dictionary<char, Color>
        {
            { 'o', Px.C("#07060f") }, { 'b', Px.C("#e8875f") }, { 'h', Px.C("#f9c0a2") }, { 's', Px.C("#b8603f") },
            { 'c', Px.C("#f39b9b") }, { 'a', Px.C("#f5c84b") }, { 'w', Color.White }, { 'k', Px.C("#07060f") }, { 'm', Px.C("#5a2414") },
        };

        static readonly string[] Base = {
            "................",
            ".......aa.......",
            ".......aa.......",
            "........o.......",
            "....oooooooo....",
            "...obbbbbbbbo...",
            "..obhhbbbbbbbo..",
            "..obhbbbbbbbbo..",
            "..obbbbbbbbbbo..",
            "..obbbbbbbbbbo..",
            "..obcbbbbbbcbo..",
            "..obbbbbbbbbbo..",
            "...obbbbbbbsso..",
            "....oooooooo....",
            "....oo....oo....",
            "................",
        };

        static readonly Dictionary<string, object[][]> Faces = new Dictionary<string, object[][]>
        {
            { "fresh", new[] { F(8,5,"kw"), F(8,9,"kw"), F(9,5,"kk"), F(9,9,"kk"), F(10,6,"m"), F(10,9,"m"), F(11,7,"mm") } },
            { "mid",   new[] { F(8,5,"kw"), F(8,9,"kw"), F(9,5,"kk"), F(9,9,"kk"), F(11,7,"mm") } },
            { "tired", new[] { F(9,5,"kk"), F(9,9,"kk"), F(11,6,"mmmm") } },
            { "ko",    new[] { F(8,5,"kb"), F(8,9,"bk"), F(9,5,"bk"), F(9,9,"kb"), F(11,7,"mm") } },
        };
        static object[] F(int r, int c, string s) { return new object[] { r, c, s }; }

        public static char[][] Map(string mood)
        {
            var rows = new char[16][];
            for (int i = 0; i < 16; i++) rows[i] = Base[i].ToCharArray();
            object[][] face;
            if (!Faces.TryGetValue(mood ?? "mid", out face)) face = Faces["mid"];
            foreach (var f in face)
            {
                int r = (int)f[0], c = (int)f[1];
                string s = (string)f[2];
                for (int i = 0; i < s.Length; i++) rows[r][c + i] = s[i];
            }
            return rows;
        }

        static Color Gray(Color c)
        {
            int g = (int)(0.3 * c.R + 0.59 * c.G + 0.11 * c.B);
            g = (int)(g * 0.85);
            return Color.FromArgb(g, g, g);
        }

        public static void Draw(Graphics g, string mood, float x, float y, float cell)
        {
            var map = Map(mood);
            bool ko = mood == "ko";
            for (int r = 0; r < 16; r++)
                for (int c = 0; c < 16; c++)
                {
                    char ch = map[r][c];
                    if (ch == '.') continue;
                    Color col = Pal[ch];
                    if (ko && ch != 'o' && ch != 'k') col = Gray(col);
                    using (var b = new SolidBrush(col))
                        g.FillRectangle(b, (float)Math.Floor(x + c * cell), (float)Math.Floor(y + r * cell), (float)Math.Ceiling(cell), (float)Math.Ceiling(cell));
                }
        }

        public static Bitmap Bitmap(string mood, int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, mood, 0, 0, size / 16f);
            }
            return bmp;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr h);

        // 트레이 아이콘 (캐릭터 얼굴)
        public static Icon MakeIcon(string mood, int size)
        {
            using (var bmp = Bitmap(mood, size))
            {
                IntPtr h = bmp.GetHicon();
                var tmp = Icon.FromHandle(h);
                var icon = (Icon)tmp.Clone();
                tmp.Dispose();
                try { DestroyIcon(h); } catch { }
                return icon;
            }
        }
    }
}
