using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UsageQuest
{
    // 바탕화면 위젯 (간단 모드 / 전체 모드). 전부 직접 그린다.
    class WidgetForm : Form
    {
        readonly AppCore core;
        float s = 1f;              // DPI 배율
        int px = 3;                // 패널 테두리 두께
        bool compact = true;
        readonly Timer anim = new Timer { Interval = 100 };
        int tick;
        bool blink;                // 0.5초마다 토글
        bool bob;                  // 캐릭터 위아래
        int typed;                 // 대화창 타자 효과
        string lastMsg = "";
        Point? hover;

        class Hit { public RectangleF R; public Action Do; public string Tip; public bool Button; }
        readonly List<Hit> hits = new List<Hit>();
        readonly ToolTip tip = new ToolTip { InitialDelay = 500, ShowAlways = true };
        string tipText;

        const int CW = 320, FW = 420, FH = 700;

        public WidgetForm(AppCore core)
        {
            this.core = core;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Px.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "Usage Quest";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Sprite.MakeIcon("mid", 32); } catch { }
            using (var g = CreateGraphics()) s = Math.Max(1f, g.DpiX / 96f);
            px = Math.Max(2, (int)Math.Round(3 * s));
            compact = Settings.GetStr("mode") != "full";
            TopMost = Settings.GetBool("pinned", false);
            anim.Tick += (a, b) => OnAnim();
            ApplySize(true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // Alt+Tab·작업표시줄에 안 나옴
                return cp;
            }
        }

        public bool Compact { get { return compact; } }

        public void SetCompact(bool c)
        {
            compact = c;
            Settings.SetStr("mode", c ? "compact" : "full");
            ApplySize(false);
            Invalidate();
        }

        int S(float v) { return (int)Math.Round(v * s); }

        // ---------- 크기·위치 ----------
        public void ApplySize(bool initial)
        {
            Size sz = compact ? new Size(S(CW), S(CompactHeight() + AgExtra())) : new Size(S(FW), S(FH + AgExtra()));
            if (initial)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                double? x = Settings.GetNum("posX"), y = Settings.GetNum("posY");
                Point p = new Point(wa.Right - sz.Width - S(16), wa.Top + S(16));
                if (x.HasValue && y.HasValue)
                {
                    var sp = new Point((int)x.Value, (int)y.Value);
                    if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(sp, new Size(80, 40))))) p = sp;
                }
                Location = p;
            }
            if (ClientSize != sz) ClientSize = sz;
            KeepOnScreen();
        }

        public void KeepOnScreen()
        {
            var wa = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Min(Math.Max(Left, wa.Left), wa.Right - Width);
            int y = Math.Min(Math.Max(Top, wa.Top), wa.Bottom - Height);
            if (x != Left || y != Top) Location = new Point(x, y);
        }

        public void ResetPosition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - S(16), wa.Top + S(16));
            Settings.SetNum("posX", null); Settings.SetNum("posY", null);
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (Visible && WindowState == FormWindowState.Normal) { saveMoveTimer.Stop(); saveMoveTimer.Start(); }
        }
        readonly Timer saveMoveTimer = MakeSaveTimer();
        static Timer MakeSaveTimer() { return new Timer { Interval = 400 }; }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            saveMoveTimer.Tick += (a, b) => { saveMoveTimer.Stop(); Settings.SetNum("posX", Left); Settings.SetNum("posY", Top); };
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) anim.Start(); else anim.Stop();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!core.Quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; core.HideWidget(); return; }
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) core.HideWidget();
            else if (e.KeyCode == Keys.R || e.KeyCode == Keys.F5) core.Refresh(true);
        }

        // ---------- 애니메이션 ----------
        void OnAnim()
        {
            tick++;
            bool dirty = false;
            string msg = core.Message ?? "";
            if (msg != lastMsg) { lastMsg = msg; typed = 0; }
            if (!compact && typed < msg.Length) { typed = Math.Min(msg.Length, typed + 3); dirty = true; }
            if (tick % 5 == 0) { blink = !blink; dirty = true; }
            if (tick % 6 == 0) { bob = !bob; dirty = true; }
            if (tick % 10 == 0) dirty = true; // 카운트다운
            if (dirty) Invalidate();
        }

        public void DataChanged()
        {
            ApplySize(false);
            Invalidate();
        }

        // ---------- 마우스 ----------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            hover = e.Location;
            var h = HitAt(e.Location);
            string t = h != null ? h.Tip : null;
            if (t != tipText) { tipText = t; tip.SetToolTip(this, t ?? ""); }
            Cursor = h != null && h.Do != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var h = HitAt(e.Location);
            if (h != null && h.Do != null) return;
            if (e.Y < S(compact ? 32 : 38)) Native.DragWindow(Handle);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { core.ShowMenuAt(Cursor.Position); return; }
            if (e.Button != MouseButtons.Left) return;
            var h = HitAt(e.Location);
            if (h != null && h.Do != null) h.Do();
        }

        Hit HitAt(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
            return null;
        }

        bool Hovered(RectangleF r) { return hover.HasValue && r.Contains(hover.Value); }

        void AddHit(RectangleF r, Action a, string t, bool button = true) { hits.Add(new Hit { R = r, Do = a, Tip = t, Button = button }); }

        // ---------- 그리기 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Px.Setup(g, s);
            hits.Clear();
            DrawBackground(g);
            if (compact) PaintCompact(g); else PaintFull(g);
            Px.Scanlines(g, ClientRectangle, s);
        }

        void DrawBackground(Graphics g)
        {
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle, Px.Bg, Px.Bg2, 90f))
                g.FillRectangle(b, ClientRectangle);
            var rnd = new Random(7);
            for (int i = 0; i < 14; i++)
            {
                int a = rnd.Next(60, 170);
                Px.Fill(g, Color.FromArgb(a, 255, 255, 255), rnd.Next(ClientSize.Width), rnd.Next(ClientSize.Height), Math.Max(1, S(1)), Math.Max(1, S(1)));
            }
        }

        // 제목줄 + 창 버튼
        float DrawTitlebar(Graphics g, float h)
        {
            float W = ClientSize.Width;
            Px.Fill(g, Px.LineDark, 0, 0, W, S(h));
            Px.Fill(g, Px.Accent, 0, S(h), W, px);
            var fb = Px.F("ps", S(compact ? 8 : 10));
            float gem = S(compact ? 8 : 10);
            float gx = S(compact ? 12 : 16), gy = S(h) / 2f;
            // 회전한 보석 (마름모)
            var pts = new[] { new PointF(gx, gy - gem / 1.4f), new PointF(gx + gem / 1.4f, gy), new PointF(gx, gy + gem / 1.4f), new PointF(gx - gem / 1.4f, gy) };
            using (var b = new SolidBrush(Px.WarnDk)) g.FillPolygon(b, pts.Select(p => new PointF(p.X + S(1), p.Y + S(1))).ToArray());
            using (var b = new SolidBrush(Px.Warn)) g.FillPolygon(b, pts);
            Px.TextShadow(g, "USAGE QUEST", fb, Px.Accent, Px.AccentDk, gx + gem + S(4), Px.MidY(fb, 0, S(h)), S(compact ? 1 : 2));

            // 버튼: 오른쪽부터 × □ ▲ (♪ 또는 ↻)
            float bw = S(compact ? 22 : 24), bh = S(compact ? 22 : 22), by = (S(h) - bh) / 2f;
            float x = W - S(8) - bw;
            WinButton(g, new RectangleF(x, by, bw, bh), "close", () => core.HideWidget(), "숨기기 (시계 옆 아이콘으로 다시 열기)", true);
            x -= bw + S(2);
            WinButton(g, new RectangleF(x, by, bw, bh), compact ? "full" : "compact", () => core.SetCompact(!compact), compact ? "전체 보기" : "간단히 보기", false);
            x -= bw + S(2);
            WinButton(g, new RectangleF(x, by, bw, bh), "pin", () => core.TogglePin(), "항상 위에 고정", false, TopMost);
            x -= bw + S(2);
            if (compact)
            {
                bool cool = core.CooldownLeft > 0 || core.Busy;
                WinButton(g, new RectangleF(x, by, bw, bh), "refresh", cool ? (Action)null : () => core.Refresh(true),
                    cool ? (core.Busy ? "확인 중…" : (int)Math.Ceiling(core.CooldownLeft) + "초 후 다시 확인 가능") : "다시 확인 (R)", false, false, cool);
            }
            else
            {
                WinButton(g, new RectangleF(x, by, bw, bh), "sound", () => core.ToggleSound(), "효과음 켜기/끄기", false, Settings.GetBool("sound", false));
            }
            // 새 버전 배지
            if (core.Update != null)
            {
                var uf = Px.F("ps", S(compact ? 7 : 8));
                string ut = core.UpdateState == "downloading" ? core.UpdatePct + "%" : core.UpdateState == "installing" ? "..." : "UP";
                float uw = Px.Measure(g, "100%", uf).Width + S(10);
                var ur = new RectangleF(x - S(6) - uw, by + S(3), uw, bh - S(6));
                bool hv = Hovered(ur);
                Px.Fill(g, Px.WarnDk, ur.X + S(2), ur.Y + S(2), ur.Width, ur.Height);
                Px.Fill(g, hv ? Color.White : (blink || core.UpdateState != null ? Px.Warn : Px.Accent), ur.X, ur.Y, ur.Width, ur.Height);
                var usz = Px.Measure(g, ut, uf);
                Px.Text(g, ut, uf, Px.LineDark, ur.X + (ur.Width - usz.Width) / 2f, Px.MidY(uf, ur.Y, ur.Height));
                string tipU = core.UpdateState == "downloading" ? "업데이트 받는 중… " + core.UpdatePct + "%"
                    : core.UpdateState == "installing" ? "설치 중… 잠시 후 다시 실행됩니다"
                    : "새 버전 " + Updater.Short(core.Update.Version) + " — 눌러서 업데이트 (현재 " + Updater.Short(Updater.Current) + ")";
                AddHit(ur, core.UpdateState == null ? (Action)(() => core.StartUpdate()) : null, tipU);
            }
            return S(h) + px;
        }

        void WinButton(Graphics g, RectangleF r, string kind, Action a, string tipT, bool danger, bool on = false, bool disabled = false)
        {
            bool hv = Hovered(r) && !disabled;
            if (hv) Px.Fill(g, danger ? Px.Bad : Px.PanelHi, r.X, r.Y, r.Width, r.Height);
            Color c = disabled ? Color.FromArgb(90, Px.InkDim) : on ? Px.Warn : hv ? (danger ? Color.White : Px.Ink) : Px.InkDim;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = Math.Max(1, S(1));
            using (var pen = new Pen(c, Math.Max(1, (float)Math.Round(1.5f * s))))
            using (var br = new SolidBrush(c))
            {
                switch (kind)
                {
                    case "close":
                        for (int i = -3; i <= 3; i++) { g.FillRectangle(br, cx + i * u * 1.2f - u / 2, cy + i * u * 1.2f - u / 2, u * 1.4f, u * 1.4f); g.FillRectangle(br, cx + i * u * 1.2f - u / 2, cy - i * u * 1.2f - u / 2, u * 1.4f, u * 1.4f); }
                        break;
                    case "full":
                        g.DrawRectangle(pen, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u);
                        break;
                    case "compact":
                        g.DrawRectangle(pen, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u);
                        g.FillRectangle(br, cx - 2.5f * u, cy - 2.5f * u, 5 * u, 5 * u);
                        break;
                    case "pin":
                        g.FillPolygon(br, new[] { new PointF(cx, cy - 6 * u), new PointF(cx + 6 * u, cy + 5 * u), new PointF(cx - 6 * u, cy + 5 * u) });
                        break;
                    case "refresh":
                        g.DrawArc(pen, cx - 5 * u, cy - 5 * u, 10 * u, 10 * u, -60, 290);
                        g.FillPolygon(br, new[] { new PointF(cx + 2 * u, cy - 8 * u), new PointF(cx + 6 * u, cy - 4.5f * u), new PointF(cx + 1 * u, cy - 2.5f * u) });
                        break;
                    case "sound":
                        g.FillRectangle(br, cx - 1 * u, cy - 6 * u, 1.6f * u, 10 * u);
                        g.FillRectangle(br, cx - 1 * u, cy - 6 * u, 5 * u, 2 * u);
                        g.FillEllipse(br, cx - 5 * u, cy + 1 * u, 5 * u, 4 * u);
                        break;
                }
            }
            AddHit(r, a, tipT);
        }

        float PanelInnerTop(float y) { return y + px * 2; }

        // ===================== 간단 모드 =====================
        int CompactHeight()
        {
            var r = core.Shown;
            float h = 32 + 3;              // 제목줄 + 선
            h += 8 + 6;                    // 위 여백 + 패널 테두리
            if (r != null && r.Ok && r.Meters.Count > 0)
                h += 10 + r.Meters.Count * 28 + (r.Meters.Count - 1) * 9 + 8 + 2 + 7 + 12 + 8;
            else if (core.Error != null)
                h += 10 + 18 + 2 * 34 + 32 + 10;
            else h += 60;
            h += 6 + 10;                   // 패널 테두리 + 아래 여백
            return (int)Math.Ceiling(h);
        }

        void PaintCompact(Graphics g)
        {
            float top = DrawTitlebar(g, 32);
            float W = ClientSize.Width;
            var panel = new RectangleF(S(8) + px * 2, top + S(8) + px * 2, W - (S(8) + px * 2) * 2, ClientSize.Height - top - S(8) - px * 4 - S(10) - S(AgExtra()));
            PaintAgBelow(g, panel, S(10));
            Px.PanelBox(g, panel, px, Px.Panel);
            float x0 = panel.X + S(10), x1 = panel.Right - S(10), y = panel.Y + S(10);

            var r = core.Shown;
            if (r == null || (!r.Ok && core.Error == null)) { Loading(g, panel); return; }
            if (core.Error != null && (core.Last == null || core.Error.Code != "RATE_LIMIT" && core.Error.Code != "NETWORK")) { PaintError(g, panel, true); return; }

            var chipF = Px.F("ps", S(7));
            var pctF = Px.F("ps", S(9));
            var cdF = Px.F("g9", S(10));
            foreach (var m in r.Meters)
            {
                Color c, cd, bc, bcd;
                Px.MeterColors(m.Key, out c, out cd);
                Px.LevelColors(m.Remaining, c, cd, out bc, out bcd);
                Px.Chip(g, m.Chip, chipF, c, cd, new RectangleF(x0, y, S(42), S(14)), S(2));
                float bx = x0 + S(50), bw = x1 - S(46) - bx;
                float bh = S(m.Main ? 14 : 12);
                Px.SegBar(g, new RectangleF(bx, y + (S(14) - bh) / 2f, bw, bh), m.Remaining, bc, bcd, 20, s * 0.5f, m.Remaining < 25, blink);
                Px.TextR(g, (int)Math.Round(m.Used) + "%", pctF, Px.Ink, x1, Px.MidY(pctF, y, S(14)));
                string cdText = m.ResetsAt.HasValue ? Fmt.Left(m.ResetsAt) : (m.Used > 0 ? "" : "아직 사용 안 함");
                Px.Text(g, cdText, cdF, Px.InkFaint, bx, y + S(16));
                AddHit(new RectangleF(x0, y, x1 - x0, S(28)), null, m.Title + " · " + (int)Math.Round(m.Used) + "% 사용" + (m.ResetsAt.HasValue ? " · " + Fmt.ResetAt(m.ResetsAt) + " 리셋" : ""), false);
                y += S(28 + 9);
            }
            y -= S(9);
            y += S(8);
            Dashed(g, x0, y, x1 - x0);
            y += S(2 + 7);
            Px.Text(g, AccountText(r) + (core.Error != null ? "  · 최근 확인 실패" : ""), Px.F("g9", S(10)), Px.InkFaint, x0, y);
        }

        // ===================== Antigravity 칸 =====================
        int AgRows() { var a = core.Ag; return a != null && a.Ok ? Math.Max(1, a.Meters.Count) : 0; }

        // Antigravity 패널이 차지하는 높이(DIP, 위 간격·테두리 포함). 안 보이면 0
        int AgExtra()
        {
            if (!core.AgVisible) return 0;
            int rows = AgRows();
            float inner = 10 + 12 + 8 + (rows > 0 ? rows * 28 + (rows - 1) * 9 : 14) + 8;
            return (int)Math.Ceiling(8 + 12 + inner);
        }

        void PaintAgBelow(Graphics g, RectangleF above, float pad)
        {
            if (!core.AgVisible) return;
            var ag = core.Ag;
            float top = above.Bottom + px * 2 + S(8) + px * 2;
            var p = new RectangleF(above.X, top, above.Width, S(AgExtra()) - S(8) - px * 4);
            Px.PanelBox(g, p, px, Px.Panel);
            float x0 = p.X + pad, x1 = p.Right - pad, y = p.Y + S(10);
            var hf = Px.F("ps", S(7));
            Px.TextShadow(g, "ANTIGRAVITY", hf, AgTeal, AgTealDk, x0, y, Math.Max(1, S(1)));
            if (ag.Ok && ag.Email != null) Px.TextR(g, Clip(g, ag.Email, Px.F("g9", S(10)), (x1 - x0) * 0.6f), Px.F("g9", S(10)), Px.InkFaint, x1, y - S(1));
            y += S(12 + 8);
            if (!ag.Ok)
            {
                string msg = ag.Code == "NOT_RUNNING" ? "Antigravity가 꺼져 있음 — 켜면 표시됩니다" : "Antigravity 확인 실패: " + ag.Message;
                Px.Text(g, Clip(g, msg, Px.F("g9", S(10)), x1 - x0), Px.F("g9", S(10)), Px.InkFaint, x0, y);
                return;
            }
            var chipF = Px.F("ps", S(7));
            var pctF = Px.F("ps", S(9));
            var cdF = Px.F("g9", S(10));
            foreach (var m in ag.Meters)
            {
                Color c = m.Chip == "GEM" ? AgTeal : m.Chip == "3P" ? AgPink : Px.Accent;
                Color cd = m.Chip == "GEM" ? AgTealDk : m.Chip == "3P" ? AgPinkDk : Px.AccentDk;
                Color bc, bcd;
                Px.LevelColors(m.Remaining, c, cd, out bc, out bcd);
                Px.Chip(g, m.Chip, chipF, c, cd, new RectangleF(x0, y, S(42), S(14)), S(2));
                float bx = x0 + S(50), bw = x1 - S(46) - bx;
                float bh = S(12);
                Px.SegBar(g, new RectangleF(bx, y + (S(14) - bh) / 2f, bw, bh), m.Remaining, bc, bcd, 20, s * 0.5f, m.Remaining < 25, blink);
                Px.TextR(g, (int)Math.Round(m.Used) + "%", pctF, Px.Ink, x1, Px.MidY(pctF, y, S(14)));
                string win = m.Title.Contains(" · ") ? m.Title.Substring(m.Title.LastIndexOf(" · ") + 3) : "";
                string cdText = (win != "" ? win + " · " : "") + (m.ResetsAt.HasValue ? Fmt.Left(m.ResetsAt) : "");
                Px.Text(g, Clip(g, cdText, cdF, x1 - bx), cdF, Px.InkFaint, bx, y + S(16));
                AddHit(new RectangleF(x0, y, x1 - x0, S(28)), null, "Antigravity · " + m.Title + " · " + (int)Math.Round(m.Used) + "% 사용" + (m.ResetsAt.HasValue ? " · " + Fmt.ResetAt(m.ResetsAt) + " 리셋" : ""), false);
                y += S(28 + 9);
            }
        }

        static readonly Color AgTeal = Px.C("#4fd1c5"), AgTealDk = Px.C("#23877d"), AgPink = Px.C("#f28bb4"), AgPinkDk = Px.C("#a8467a");

        static string AccountText(UsageResult r)
        {
            var a = r.Account;
            string who = a != null ? (a.Email ?? a.Name) : null;
            return (who ?? "Claude Code 로그인 계정") + " · " + (r.Plan ?? "CLAUDE");
        }

        void Dashed(Graphics g, float x, float y, float w)
        {
            float d = S(6), gp = S(4);
            for (float i = 0; i < w; i += d + gp) Px.Fill(g, Px.Groove, x + i, y, Math.Min(d, w - i), Math.Max(1, S(2)));
        }

        void Loading(Graphics g, RectangleF panel)
        {
            var f = Px.F("ps", S(10));
            string dots = new string('.', (tick / 3) % 4);
            string t = "LOADING" + dots;
            var sz = Px.Measure(g, "LOADING...", f);
            Px.Text(g, t, f, Px.InkDim, panel.X + (panel.Width - sz.Width) / 2f, panel.Y + (panel.Height - f.Size) / 2f);
        }

        // ---------- 오류 화면 ----------
        void PaintError(Graphics g, RectangleF panel, bool small)
        {
            var e = core.Error;
            string title; string[] lines; bool warn = false;
            var actions = new List<KeyValuePair<string, Action>>();
            Action retry = () => core.Refresh(true);
            switch (e.Code)
            {
                case "NO_CREDENTIALS":
                    title = "로그인 정보 없음";
                    lines = new[] { "Claude Code에 Pro/Max 구독 계정으로 로그인되어 있어야 합니다.", "터미널에서 claude 실행 → /login 으로 로그인한 뒤 다시 확인하세요.", "WSL 안에서 Claude Code를 쓴다면 WSL 쪽 .credentials.json 파일을 직접 지정하세요." };
                    actions.Add(new KeyValuePair<string, Action>("파일 직접 지정", () => core.PickCredentials()));
                    actions.Add(new KeyValuePair<string, Action>("다시 확인", retry));
                    break;
                case "EXPIRED":
                    warn = true;
                    if (core.Renewing)
                    {
                        title = "토큰 갱신 중" + new string('.', (tick / 3) % 4);
                        lines = new[] { "Claude Code를 창 없이 잠깐 실행해서 토큰을 갱신하고 있습니다.", "보통 몇 초, 길면 30초 정도 걸립니다." };
                    }
                    else
                    {
                        title = "토큰 만료";
                        lines = core.RenewNote != null
                            ? new[] { "자동 갱신 실패: " + core.RenewNote, "다시 시도해도 안 되면 터미널에서 claude 를 실행해 /login 으로 다시 로그인하세요." }
                            : new[] { "로그인 토큰이 만료되었습니다.", "토큰 갱신을 누르면 Claude Code를 창 없이 잠깐 실행해서 자동으로 갱신합니다." };
                        actions.Add(new KeyValuePair<string, Action>("토큰 갱신", () => core.StartRenew()));
                        actions.Add(new KeyValuePair<string, Action>("다시 확인", retry));
                    }
                    break;
                case "RATE_LIMIT":
                    title = "잠깐 휴식"; warn = true;
                    lines = new[] { "확인 요청이 너무 잦아 서버가 잠시 막았습니다.", "10분 뒤 자동으로 다시 시도합니다." };
                    break;
                case "NETWORK":
                    title = "연결 실패";
                    lines = new[] { "서버에 연결하지 못했습니다.", "인터넷 연결이나 방화벽·프록시 설정을 확인하세요." };
                    actions.Add(new KeyValuePair<string, Action>("다시 확인", retry));
                    break;
                default:
                    title = "알 수 없는 응답";
                    lines = new[] { e.Message ?? "알 수 없는 오류", "/usage 엔드포인트는 비공식이라 Claude Code 업데이트로 바뀌었을 수 있습니다." };
                    actions.Add(new KeyValuePair<string, Action>("다시 확인", retry));
                    break;
            }
            float x0 = panel.X + S(10), w = panel.Width - S(20), y = panel.Y + S(10);
            var tf = Px.F("g11b", S(small ? 13 : 16));
            Px.Text(g, title, tf, warn ? Px.Warn : Px.Bad, x0, y);
            y += S(small ? 20 : 26);
            var lf = Px.F(small ? "g9" : "g11", S(small ? 10 : 12));
            int max = small ? 2 : lines.Length;
            for (int i = 0; i < Math.Min(max, lines.Length); i++)
            {
                var rect = new RectangleF(x0, y, w, S(200));
                var sz = g.MeasureString(lines[i], lf, (int)w);
                using (var b = new SolidBrush(Px.InkDim)) g.DrawString(lines[i], lf, b, rect);
                y += sz.Height + S(small ? 4 : 8);
            }
            y += S(4);
            float bx = x0;
            var bf = Px.F("g11b", S(small ? 11 : 12));
            foreach (var a in actions)
            {
                var sz = Px.Measure(g, a.Key, bf);
                var r = new RectangleF(bx, y, sz.Width + S(18), S(small ? 24 : 28));
                GhostButton(g, r, a.Key, bf, a.Value);
                bx += r.Width + S(10);
            }
        }

        void GhostButton(Graphics g, RectangleF r, string text, Font f, Action a)
        {
            bool hv = Hovered(r);
            Px.Fill(g, Color.FromArgb(17, 15, 34), r.X + S(3), r.Y + S(3), r.Width, r.Height);
            Px.Fill(g, Px.LineDark, r.X - S(2), r.Y - S(2), r.Width + S(4), r.Height + S(4));
            Px.Fill(g, hv ? Color.FromArgb(58, 52, 110) : Px.PanelHi, r.X, r.Y, r.Width, r.Height);
            var sz = Px.Measure(g, text, f);
            Px.Text(g, text, f, Px.Ink, r.X + (r.Width - sz.Width) / 2f, Px.MidY(f, r.Y, r.Height));
            AddHit(r, a, null);
        }

        // ===================== 전체 모드 =====================
        void PaintFull(Graphics g)
        {
            float top = DrawTitlebar(g, 38);
            float W = ClientSize.Width, H = ClientSize.Height;
            float m = S(14) + px * 2;
            var mood = core.Mood;
            var r = core.Shown;

            // 히어로
            var hero = new RectangleF(m, top + S(14) + px * 2, W - m * 2, S(104));
            Px.PanelBox(g, hero, px, Px.Panel);
            float sx = hero.X + S(14), sy = hero.Y + S(12);
            string face = mood != null ? mood.Face : "mid";
            float off = face == "ko" ? S(6) : (bob ? -S(face == "tired" ? 2 : 4) : 0);
            Px.Fill(g, Color.FromArgb(136, 0, 0, 0), sx + S(16), sy + S(80), S(48), S(6));
            Sprite.Draw(g, face, sx, sy + off, S(5));
            if (face == "tired") Drop(g, sx + S(78), sy + (tick % 16) * S(0.6f));
            if (face == "ko") Px.Text(g, (tick / 5) % 2 == 0 ? "Zz" : "zZ", Px.F("ps", S(10)), Px.InkDim, sx + S(72), sy - S(2));

            float ix = sx + S(100), iy = hero.Y + S(14);
            var badgeF = Px.F("ps", S(8));
            string plan = r != null && r.Ok ? (r.Plan ?? "CLAUDE") : "OFFLINE";
            float bw = Px.Measure(g, plan, badgeF).Width + S(12);
            Badge(g, plan, badgeF, Px.Accent, Px.AccentDk, new RectangleF(ix, iy, bw, S(16)));
            if (core.DemoMode) Badge(g, "DEMO", badgeF, Px.Violet, Px.VioletDk, new RectangleF(ix + bw + S(8), iy, Px.Measure(g, "DEMO", badgeF).Width + S(12), S(16)));
            iy += S(24);
            var acct = r != null && r.Ok ? r.Account : (core.Last != null ? core.Last.Account : null);
            string name = acct != null ? (acct.Name ?? (acct.Email != null ? acct.Email.Split('@')[0] : null)) : null;
            Px.Text(g, Clip(g, name ?? "클로드 모험가", Px.F("g11b", S(16)), hero.Right - ix - S(10)), Px.F("g11b", S(16)), Px.Ink, ix, iy);
            iy += S(21);
            if (acct != null && acct.Email != null)
            {
                Px.Text(g, Clip(g, acct.Email + (acct.Org != null ? " · " + acct.Org : ""), Px.F("g9", S(11)), hero.Right - ix - S(10)), Px.F("g9", S(11)), Px.InkFaint, ix, iy);
                iy += S(16);
            }
            var sf = Px.F("g11", S(13));
            Px.Text(g, "상태", sf, Px.InkDim, ix, iy);
            Color mc = mood == null ? Px.Ink : mood.Level == "bad" ? Px.Bad : mood.Level == "warn" ? Px.Warn : Px.Good;
            Px.Text(g, mood != null ? mood.Label : "확인 중…", Px.F("g11b", S(13)), mc, ix + Px.Measure(g, "상태 ", sf).Width + S(2), iy);

            // 하단(대화창, 버튼) 먼저 위치 계산
            float footerH = S(62);
            var dialog = new RectangleF(m, H - footerH - S(70) - px * 2, W - m * 2, S(64));
            var stats = new RectangleF(m, hero.Bottom + px * 2 + S(12) + px * 2, W - m * 2, dialog.Y - px * 4 - S(12) - S(AgExtra()) - (hero.Bottom + px * 2 + S(12) + px * 2));
            PaintAgBelow(g, stats, S(14));
            Px.PanelBox(g, stats, px, Px.Panel);
            if (r == null || (!r.Ok && core.Error == null)) Loading(g, stats);
            else if (core.Error != null && (core.Last == null || core.Error.Code != "RATE_LIMIT" && core.Error.Code != "NETWORK")) PaintError(g, stats, false);
            else PaintFullMeters(g, stats, r);

            // 대화창
            Px.PanelBox(g, dialog, px, Px.Dialog);
            string msg = core.Message ?? "";
            string shown = msg.Substring(0, Math.Min(typed, msg.Length));
            using (var b = new SolidBrush(Px.Ink))
                g.DrawString(shown, Px.F("g11", S(13)), b, new RectangleF(dialog.X + S(14), dialog.Y + S(10), dialog.Width - S(40), dialog.Height - S(14)));
            if (blink) Px.Text(g, "▼", Px.F("g11", S(10)), Px.Accent, dialog.Right - S(18), dialog.Bottom - S(16));

            // 하단 버튼 + 정보
            float fy = H - footerH + S(14);
            bool cool = core.CooldownLeft > 0 || core.Busy;
            string bt = core.Busy ? "확인 중…" : cool ? (int)Math.Ceiling(core.CooldownLeft) + "초 후 가능" : "다시 확인";
            var bf = Px.F("g11b", S(13));
            var br = new RectangleF(S(17), fy, Math.Max(S(104), Px.Measure(g, bt, bf).Width + S(28)), S(32));
            PixelButton(g, br, bt, bf, cool ? (Action)null : () => core.Refresh(true), cool);
            var mf = Px.F("g9", S(11));
            string upd = core.Last != null ? (core.Error != null ? "마지막 성공 " : "마지막 확인 ") + Fmt.Clock(core.Last.FetchedAt) : "—";
            Px.TextR(g, upd, mf, Px.InkDim, W - S(17), fy + S(1));
            var left = core.NextAt - DateTime.UtcNow;
            string nxt = core.NextAt == DateTime.MinValue ? "자동 확인 대기" : "다음 자동 확인 " + (int)Math.Max(0, left.TotalMinutes) + ":" + Math.Max(0, left.Seconds).ToString("00");
            Px.TextR(g, nxt, mf, Px.InkFaint, W - S(17), fy + S(18));
        }

        // 땀방울 (픽셀)
        void Drop(Graphics g, float x, float y)
        {
            float u = S(2);
            string[] map = { "..x..", ".xxx.", "xxxxx", "xxwxx", ".xxx." };
            for (int r = 0; r < map.Length; r++)
                for (int c = 0; c < 5; c++)
                    if (map[r][c] != '.') Px.Fill(g, map[r][c] == 'w' ? Color.White : Px.Sky, x + c * u, y + r * u, u, u);
        }

        string Clip(Graphics g, string t, Font f, float w)
        {
            if (Px.Measure(g, t, f).Width <= w) return t;
            while (t.Length > 1 && Px.Measure(g, t + "…", f).Width > w) t = t.Substring(0, t.Length - 1);
            return t + "…";
        }

        void Badge(Graphics g, string t, Font f, Color c, Color cd, RectangleF r)
        {
            Px.Fill(g, cd, r.X + S(2), r.Y + S(2), r.Width, r.Height);
            Px.Fill(g, c, r.X, r.Y, r.Width, r.Height);
            Px.Text(g, t, f, Px.LineDark, r.X + S(6), Px.MidY(f, r.Y, r.Height));
        }

        void PixelButton(Graphics g, RectangleF r, string t, Font f, Action a, bool disabled)
        {
            bool hv = Hovered(r) && !disabled;
            Color face = disabled ? C(0x5a5378) : hv ? C(0xf59a73) : Px.Accent;
            Color sh = disabled ? Px.Groove : Px.AccentDk;
            Px.Fill(g, Px.LineDark, r.X + S(2), r.Y + S(2), r.Width + S(4), r.Height + S(4));
            Px.Fill(g, sh, r.X + S(4), r.Y + S(4), r.Width + S(2), r.Height + S(2));
            Px.Fill(g, Px.LineDark, r.X - S(2), r.Y - S(2), r.Width + S(4), r.Height + S(4));
            Px.Fill(g, face, r.X, r.Y, r.Width, r.Height);
            var sz = Px.Measure(g, t, f);
            Px.Text(g, t, f, disabled ? C(0x2a2548) : Px.LineDark, r.X + (r.Width - sz.Width) / 2f, Px.MidY(f, r.Y, r.Height));
            AddHit(r, a, disabled ? null : "다시 확인 (R)");
        }

        static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        void PaintFullMeters(Graphics g, RectangleF panel, UsageResult r)
        {
            float x0 = panel.X + S(14), x1 = panel.Right - S(14), y = panel.Y + S(12);
            var chipF = Px.F("ps", S(8));
            var titleF = Px.F("g11", S(13));
            var titleB = Px.F("g11b", S(13));
            var usedL = Px.F("g11b", S(13));
            var usedN = Px.F("ps", S(10));
            var footF = Px.F("g9", S(11));
            int idx = 0;
            foreach (var m in r.Meters)
            {
                if (idx == 1) { Dashed(g, x0, y - S(2), x1 - x0); y += S(12); }
                Color c, cd, bc, bcd;
                Px.MeterColors(m.Key, out c, out cd);
                Px.LevelColors(m.Remaining, c, cd, out bc, out bcd);
                float cw = Math.Max(S(40), Px.Measure(g, m.Chip, chipF).Width + S(10));
                Px.Chip(g, m.Chip, chipF, c, cd, new RectangleF(x0, y, cw, S(16)), S(2));
                string num = (int)Math.Round(m.Used) + "%";
                float nw = Px.Measure(g, num, usedN).Width;
                Px.TextR(g, num, usedN, Px.Ink, x1, Px.MidY(usedN, y, S(16)));
                Px.TextR(g, "사용", usedL, Px.InkDim, x1 - nw - S(4), Px.MidY(usedL, y, S(16)));
                Px.Text(g, m.Title, m.Main ? titleB : titleF, Px.Ink, x0 + cw + S(10), Px.MidY(titleF, y, S(16)));
                y += S(22);
                float bh = S(m.Main ? 24 : 18);
                Px.SegBar(g, new RectangleF(x0, y, x1 - x0, bh), m.Remaining, bc, bcd, 20, s, m.Remaining < 25, blink);
                y += bh + S(6);
                string cdText = m.ResetsAt.HasValue ? Fmt.Left(m.ResetsAt) : (m.Used > 0 ? "" : "아직 사용 안 함");
                Px.Text(g, cdText, footF, Px.InkDim, x0, y);
                Px.TextR(g, Fmt.ResetAt(m.ResetsAt), footF, Px.InkFaint, x1, y);
                y += S(16 + 10);
                idx++;
            }
            if (r.ExtraEnabled.HasValue)
            {
                Px.Fill(g, Px.WarnDk, x0 + S(1), y + S(3), S(9), S(9));
                Px.Fill(g, Px.Warn, x0, y + S(2), S(9), S(9));
                Px.Text(g, "추가 사용 크레딧", footF, Px.InkDim, x0 + S(15), y);
                string ex = r.ExtraEnabled.Value ? "켜짐" + (r.ExtraUtil.HasValue ? " · 한도의 " + (int)Math.Round(r.ExtraUtil.Value) + "% 사용" : "") : "꺼짐";
                Px.TextR(g, ex, footF, Px.InkDim, x1, y);
            }
        }
    }
}
