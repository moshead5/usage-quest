using System;
using System.Drawing;
using System.Windows.Forms;

namespace UsageQuest
{
    // 작업표시줄 칸: HP / STA 두 줄
    // 기본: 작업표시줄 창(Shell_TrayWnd) 안에 자식 창으로 끼워 넣기
    //  → 작업표시줄을 눌러도 안 가려지고, 전체 화면 앱에서는 작업표시줄과 함께 숨겨짐
    // 실패하거나 끄면: 작업표시줄 위에 '항상 위' 창으로 겹쳐 띄우기
    class StripForm : Form
    {
        readonly AppCore core;
        readonly float s;
        public bool Embedded { get; private set; }
        public string EmbedError { get; private set; }
        IntPtr taskbar = IntPtr.Zero;
        readonly Timer timer = new Timer { Interval = 80 };
        int tick;
        int dragLastX = int.MinValue;
        double? dragFromRight;
        bool hovered;
        readonly ToolTip tip = new ToolTip { InitialDelay = 400, ShowAlways = true };
        string tipText;

        public StripForm(AppCore core)
        {
            this.core = core;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Px.Bg2;
            DoubleBuffered = true;
            Text = "Usage Quest Taskbar";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            using (var g = CreateGraphics()) s = Math.Max(1f, g.DpiX / 96f);
            Size = new Size(S(198), S(40));
            Location = new Point(-10000, -10000);
            timer.Tick += (a, b) => OnTick();
        }

        int S(float v) { return (int)Math.Round(v * s); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE; // 클릭해도 포커스를 뺏지 않음
                return cp;
            }
        }

        public bool Start(bool wantEmbed)
        {
            Show();
            Embedded = false;
            EmbedError = null;
            if (wantEmbed && Native.IsWindows) TryEmbed();
            if (!Embedded) { TopMost = true; }
            Place();
            timer.Start();
            return Embedded;
        }

        void TryEmbed()
        {
            try
            {
                taskbar = Native.Taskbar();
                if (taskbar == IntPtr.Zero) { EmbedError = "작업표시줄을 찾지 못함"; return; }
                var tr = Native.WindowRect(taskbar);
                if (!tr.HasValue || tr.Value.Height > tr.Value.Width) { EmbedError = "세로 작업표시줄"; return; }
                long st = Native.GetStyle(Handle);
                st = (st | Native.WS_CHILD) & ~(long)Native.WS_POPUP;
                Native.SetStyle(Handle, st);
                Native.SetParent(Handle, taskbar);
                if (Native.GetParent(Handle) != taskbar) { EmbedError = "SetParent 실패"; Native.SetStyle(Handle, (st & ~(long)Native.WS_CHILD) | Native.WS_POPUP); return; }
                Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                Embedded = true;
            }
            catch (Exception e) { EmbedError = e.Message; Embedded = false; }
        }

        public void Detach()
        {
            if (!Embedded) return;
            try
            {
                Native.SetParent(Handle, IntPtr.Zero);
                long st = Native.GetStyle(Handle);
                Native.SetStyle(Handle, (st & ~(long)Native.WS_CHILD) | Native.WS_POPUP);
            }
            catch { }
            Embedded = false;
        }

        // 탐색기가 다시 시작되면 작업표시줄 창이 바뀐다
        public bool TaskbarGone
        {
            get { return Embedded && (!Native.IsWindow(taskbar) || Native.Taskbar() != taskbar); }
        }

        double FromRight()
        {
            if (dragFromRight.HasValue) return dragFromRight.Value;
            double? v = Settings.GetNum("stripFromRight");
            return v.HasValue ? v.Value : 380; // 기본: 시계·알림 아이콘 왼쪽 (DIP)
        }

        int ContentWidth() { return S(198); }

        // 작업표시줄 사각형 (화면 좌표, 물리 픽셀)
        Rectangle? TaskbarRect()
        {
            if (Embedded) return Native.WindowRect(taskbar);
            var sc = Screen.PrimaryScreen;
            var b = sc.Bounds; var w = sc.WorkingArea;
            int bottom = b.Bottom - w.Bottom, topGap = w.Top - b.Top;
            if (bottom >= S(24)) return new Rectangle(b.Left, w.Bottom, b.Width, bottom);
            if (topGap >= S(24)) return new Rectangle(b.Left, b.Top, b.Width, topGap);
            return null;
        }

        public void Place()
        {
            var tr = TaskbarRect();
            int w = ContentWidth();
            if (!tr.HasValue)
            {
                var b = Screen.PrimaryScreen.Bounds;
                Bounds = new Rectangle(b.Right - w - S(12), b.Bottom - S(42) - S(12), w, S(42));
                return;
            }
            var t = tr.Value;
            int h = Math.Max(S(22), Math.Min(t.Height - S(4), S(42)));
            int x = (int)Math.Round(t.Width - (FromRight() * s) - w);
            x = Math.Max(0, Math.Min(x, t.Width - w));
            int y = (t.Height - h) / 2;
            if (Embedded)
                Native.SetWindowPos(Handle, Native.HWND_TOP, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            else
                Bounds = new Rectangle(t.Left + x, t.Top + y, w, h);
            Invalidate();
        }

        void OnTick()
        {
            tick++;
            if (tick % 375 == 0) Invalidate(); // 30초마다 남은 시간 갱신
            if (Embedded)
            {
                if (tick % 12 == 0) Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                return;
            }
            if (!Native.IsWindows) return;
            // 겹쳐 띄우기: 마우스가 작업표시줄 위에 있으면 자주 맨 위로 (작업표시줄을 누르면 가려지므로)
            var tr = TaskbarRect();
            var p = Cursor.Position;
            bool over = tr.HasValue && p.X >= tr.Value.Left && p.X < tr.Value.Right && p.Y >= tr.Value.Top - 4 && p.Y < tr.Value.Bottom;
            if (over || tick % 12 == 0)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void DataChanged() { Invalidate(); }

        // ---------- 마우스 ----------
        bool OnGrip(Point p) { return p.X < S(16); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && OnGrip(e.Location)) { dragLastX = Cursor.Position.X; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!hovered) { hovered = true; Invalidate(); }
            Cursor = OnGrip(e.Location) ? Cursors.SizeWE : Cursors.Hand;
            if (dragLastX != int.MinValue)
            {
                int x = Cursor.Position.X;
                int dx = x - dragLastX;
                if (dx != 0)
                {
                    dragLastX = x;
                    var tr = TaskbarRect();
                    double max = tr.HasValue ? Math.Max(0, (tr.Value.Width - ContentWidth()) / s) : 2000;
                    dragFromRight = Math.Max(0, Math.Min(max, FromRight() - dx / s));
                    Place();
                }
                return;
            }
            string t = TipText();
            if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragLastX != int.MinValue)
            {
                dragLastX = int.MinValue;
                Capture = false;
                if (dragFromRight.HasValue) Settings.SetNum("stripFromRight", Math.Round(dragFromRight.Value));
                dragFromRight = null;
                return;
            }
            if (e.Button == MouseButtons.Right) core.ShowMenuAt(Cursor.Position);
            else if (e.Button == MouseButtons.Left) core.ToggleWidget();
        }

        public void ResetPosition() { Settings.SetNum("stripFromRight", null); Place(); }

        string TipText()
        {
            var r = core.Last;
            string t = "";
            if (r != null)
            {
                var hp = r.Get("five_hour"); var wk = r.Get("seven_day");
                if (hp != null) t += "HP (세션) " + (int)Math.Round(hp.Used) + "% 사용 · " + Fmt.LeftShort(hp.ResetsAt) + " 후 리셋\n";
                if (wk != null) t += "STA (주간 전체) " + (int)Math.Round(wk.Used) + "% 사용 · " + Fmt.LeftShort(wk.ResetsAt) + " 후 리셋\n";
                if (core.Error != null) t += "(최근 확인 실패, 이전 값)\n";
            }
            return t + "\n클릭: 위젯 열기/닫기 · 우클릭: 메뉴 · 왼쪽 손잡이: 옮기기";
        }

        // ---------- 그리기 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Px.Setup(g, s);
            int W = ClientSize.Width, H = ClientSize.Height;
            Px.Fill(g, Px.Bg2, 0, 0, W, H);
            using (var pen = new Pen(hovered ? Px.Accent : Px.Groove, Math.Max(1, S(2))))
                g.DrawRectangle(pen, pen.Width / 2f, pen.Width / 2f, W - pen.Width, H - pen.Width);

            // 손잡이 점
            for (int yy = S(5); yy < H - S(4); yy += S(5))
                for (int xx = S(4); xx <= S(9); xx += S(5))
                    Px.Fill(g, Px.InkFaint, xx, yy, Math.Max(1, S(1.6f)), Math.Max(1, S(1.6f)));

            bool tight = H < S(34), tiny = H < S(27);
            var r = core.Last;
            if (r == null)
            {
                string label = core.Renewing ? "토큰 갱신 중" : core.Error != null ? ShortError(core.Error.Code) : "확인 중";
                var f = Px.F("g11b", S(12));
                var sz = Px.Measure(g, label, f);
                Px.Text(g, label, f, Px.Warn, S(16) + (W - S(16) - sz.Width) / 2f, Px.MidY(f, 0, H));
                return;
            }
            bool stale = core.Error != null;
            var hp = r.Get("five_hour"); var wk = r.Get("seven_day");
            int rows = wk != null ? 2 : 1;
            float rowH = tiny ? S(9) : tight ? S(10) : S(12);
            float gap = tight ? S(1) : S(3);
            float total = rows * rowH + (rows - 1) * gap;
            float y = (H - total) / 2f;
            Row(g, "HP", Px.Good, Px.GoodDk, hp, y, rowH, tight, tiny, stale);
            if (wk != null) Row(g, "STA", Px.Sky, Px.SkyDk, wk, y + rowH + gap, rowH, tight, tiny, stale);
        }

        static string ShortError(string code)
        {
            switch (code)
            {
                case "NO_CREDENTIALS": return "로그인 필요";
                case "EXPIRED": return "토큰 만료";
                case "RATE_LIMIT": return "잠시 대기";
                case "NETWORK": return "연결 실패";
                default: return "오류";
            }
        }

        void Row(Graphics g, string chip, Color c, Color cd, Meter m, float y, float h, bool tight, bool tiny, bool stale)
        {
            float x = S(19);
            // 칩
            var cf = tiny ? Px.F("g9", S(9)) : Px.F("ps", S(7));
            Px.Chip(g, chip, cf, c, cd, new RectangleF(x, y, S(28), h), Math.Max(1, S(1)));
            x += S(28 + 5);
            // 막대 (남은 양)
            float bh = tiny ? S(5) : tight ? S(6) : S(7);
            float by = y + (h - bh) / 2f;
            Px.Fill(g, Px.LineDark, x, by, S(56), bh);
            using (var p = new Pen(Px.Groove, 1)) g.DrawRectangle(p, x - 0.5f, by - 0.5f, S(56), bh);
            if (m != null)
            {
                Color bc, bcd;
                Px.LevelColors(m.Remaining, Px.Good, Px.GoodDk, out bc, out bcd);
                Px.Fill(g, bc, x, by, (float)(S(56) * m.Remaining / 100.0), bh);
            }
            x += S(56 + 5);
            // 사용 %
            var pf = Px.F("g11b", S(tiny ? 10 : tight ? 11 : 12));
            string pct = m != null ? (int)Math.Round(m.Used) + "%" : "--";
            Color pc = m != null && m.Remaining < 25 ? Px.Bad : stale ? Px.InkFaint : Px.Ink;
            Px.TextR(g, pct, pf, pc, x + S(32), Px.MidY(pf, y, h));
            x += S(32 + 5);
            // 남은 시간
            var lf = Px.F("g9", S(tiny ? 9 : 10));
            if (m != null) Px.TextR(g, Fmt.LeftShort(m.ResetsAt), lf, Px.InkFaint, x + S(40), Px.MidY(lf, y, h));
        }
    }
}
