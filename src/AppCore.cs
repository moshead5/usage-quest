using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WTimer = System.Windows.Forms.Timer;

namespace UsageQuest
{
    // 전체 흐름: 사용량 확인 주기, 트레이 아이콘·메뉴, 위젯, 작업표시줄 칸
    class AppCore : ApplicationContext
    {
        const int AutoMs = 3 * 60 * 1000;          // 자동 확인 주기
        const int RateLimitMs = 10 * 60 * 1000;    // 429 받으면 대기
        const double ManualCooldownSec = 20;       // 수동 확인 연타 방지

        public readonly string DemoVariant;
        public bool DemoMode { get { return DemoVariant != null; } }
        public bool Quitting { get; private set; }

        public UsageResult Last;        // 마지막 성공
        public UsageResult Error;       // 현재 오류 (없으면 null)
        public UsageResult Shown { get { return Error != null && Last == null ? Error : (Last ?? Error); } }
        public Mood Mood;
        public string Message = "모험가의 상태를 살펴보는 중…";
        public bool Busy;
        public Updater.Info Update;     // 새 버전 정보 (없으면 null)
        public string UpdateState;      // null | downloading | installing
        public int UpdatePct;
        readonly WTimer updateTimer = new WTimer();
        public bool Renewing;           // 토큰 갱신 중 (claude를 창 없이 실행)
        public string RenewNote;        // 마지막 갱신 실패 이유
        DateTime lastAutoRenew = DateTime.MinValue;
        public DateTime NextAt = DateTime.MinValue;
        DateTime lastManual = DateTime.MinValue;
        double? prevHp;

        readonly Control ui;            // UI 스레드로 돌아오기 위한 핸들
        readonly WidgetForm widget;
        StripForm strip;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly WTimer refreshTimer = new WTimer();
        readonly WTimer watchTimer = new WTimer { Interval = 2000 };
        string trayFace = null;

        public double CooldownLeft { get { return Math.Max(0, ManualCooldownSec - (DateTime.UtcNow - lastManual).TotalSeconds); } }

        public AppCore(string demo, bool startedHidden)
        {
            DemoVariant = demo;
            ui = new Control();
            ui.CreateControl();
            var h = ui.Handle;

            widget = new WidgetForm(this);
            tray = new NotifyIcon { Text = "Usage Quest", Visible = true };
            SetTrayFace("mid");
            tray.MouseUp += (a, e) =>
            {
                if (e.Button == MouseButtons.Left) ToggleWidget();
            };
            tray.ContextMenuStrip = menu;
            menu.Opening += (a, e) => BuildMenu();
            BuildMenu();

            refreshTimer.Tick += (a, b) => { refreshTimer.Stop(); Refresh(false); };
            watchTimer.Tick += (a, b) => Watch();
            watchTimer.Start();

            // 업데이트 확인: 앱이 켜지고 20초 뒤 한 번만 (그 외에는 트레이 메뉴 "업데이트 확인"을 눌렀을 때)
            updateTimer.Interval = 20000;
            updateTimer.Tick += (a, b) => { updateTimer.Stop(); if (AutoUpdateCheck) CheckUpdate(false); };
            if (!DemoMode) updateTimer.Start();
            if (DemoMode && Array.IndexOf(Environment.GetCommandLineArgs(), "--fake-update") >= 0) Update = new Updater.Info { Version = new Version(9, 0, 0) };
            tray.BalloonTipClicked += (a, b) => { if (Update != null && UpdateState == null) StartUpdate(); };

            // 처음 실행이면 자동 실행 켜기 (트레이 메뉴에서 끌 수 있음)
            if (!Settings.GetBool("autoStartInit", false) && Native.IsWindows && !DemoMode)
            {
                SetAutoStart(true);
                Settings.SetBool("autoStartInit", true);
            }
            RemoveOldElectronAutostart();

            if (!(startedHidden && Settings.GetBool("hidden", false))) ShowWidget();
            if (StripEnabled) CreateStrip();
            Refresh(false);
        }

        // ---------- 사용량 확인 ----------
        public void Refresh(bool manual)
        {
            if (Busy) return;
            if (manual)
            {
                if (CooldownLeft > 0) return;
                lastManual = DateTime.UtcNow;
            }
            Busy = true;
            Changed();
            string path = Settings.GetStr("credentialsPath");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UsageResult r;
                try { r = DemoMode ? UsageClient.Demo(DemoVariant) : UsageClient.Fetch(path); }
                catch (Exception e) { r = UsageResult.Fail("NETWORK", e.Message); }
                try { ui.BeginInvoke((Action)(() => { OnResult(r); })); } catch { }
            });
        }

        void OnResult(UsageResult r)
        {
            Busy = false;
            if (r.Ok && r.Meters.Count > 0)
            {
                Last = r;
                Error = null;
                Mood = Story.For(r);
                var hp = r.Get("five_hour");
                double? hpLeft = hp != null ? hp.Remaining : (double?)null;
                if (prevHp.HasValue && hpLeft.HasValue && hpLeft.Value - prevHp.Value >= 20) { Sfx.Heal(); Message = "HP가 회복되었다! " + Mood.Text; }
                else
                {
                    if (prevHp.HasValue && hpLeft.HasValue && prevHp.Value - hpLeft.Value >= 10) Sfx.Hurt(); else Sfx.Blip();
                    Message = Mood.Text;
                }
                prevHp = hpLeft;
                Schedule(AutoMs);
            }
            else if (r.Ok)
            {
                Error = UsageResult.Fail("EMPTY", "이 계정에는 구독 사용량 정보가 없습니다. API 키 로그인 또는 Enterprise 플랜은 /usage 한도가 제공되지 않습니다.");
                Mood = new Mood { Face = "mid", Label = "정보 없음", Level = "warn" };
                Message = "표시할 사용량 정보가 없다.";
                Schedule(AutoMs);
            }
            else
            {
                Error = r;
                switch (r.Code)
                {
                    case "NO_CREDENTIALS": Mood = new Mood { Face = "ko", Label = "미접속", Level = "bad" }; Message = "모험가를 찾을 수 없다… 먼저 Claude Code에 로그인하자."; break;
                    case "EXPIRED":
                        Mood = new Mood { Face = "tired", Label = "재접속 필요", Level = "warn" };
                        Message = "통행증이 만료되었다. 갱신을 시도하자.";
                        // 자동 갱신 (30분에 한 번까지만 자동으로 시도)
                        if (AutoRenew && !Renewing && !DemoMode && (DateTime.UtcNow - lastAutoRenew).TotalMinutes >= 30)
                        {
                            lastAutoRenew = DateTime.UtcNow;
                            StartRenew();
                        }
                        break;
                    case "RATE_LIMIT": Message = "너무 자주 문을 두드렸다. 잠시 쉬었다 가자."; break;
                    case "NETWORK": Message = "통신이 끊겼다…"; break;
                    default: Message = "수상한 기운이 느껴진다… 잠시 후 다시 시도해 보자."; break;
                }
                // 일시적 오류면 이전 값을 계속 보여준다
                if (!(r.Code == "RATE_LIMIT" || r.Code == "NETWORK")) Last = null;
                Schedule(r.Code == "RATE_LIMIT" ? RateLimitMs : AutoMs);
            }
            UpdateTray();
            Changed();
        }

        // ---------- 업데이트 ----------
        bool AutoUpdateCheck { get { return Settings.GetBool("autoUpdateCheck", true); } }

        public void CheckUpdate(bool manual)
        {
            if (UpdateState != null) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Info info = null; string err = null;
                try { info = Updater.Check(); } catch (Exception e) { err = e.Message; }
                try
                {
                    ui.BeginInvoke((Action)(() =>
                    {
                        if (info != null)
                        {
                            bool isNew = Update == null || Update.Version != info.Version;
                            Update = info;
                            Changed();
                            if (isNew || manual)
                                Balloon("새 버전 " + Updater.Short(info.Version) + " 이 나왔습니다", "여기를 누르거나 위젯의 UP 버튼을 누르면 바로 업데이트합니다.");
                        }
                        else if (manual)
                            Balloon(err == null ? "최신 버전입니다" : "업데이트 확인 실패", err == null ? "현재 " + Updater.Short(Updater.Current) + " 버전을 쓰고 있습니다." : err);
                    }));
                }
                catch { }
            });
        }

        void Balloon(string title, string text)
        {
            try { tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); } catch { }
        }

        public void StartUpdate()
        {
            if (Update == null || UpdateState != null) return;
            var info = Update;
            UpdateState = "downloading";
            UpdatePct = 0;
            Changed();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string path = null, err = null;
                try
                {
                    path = Updater.Download(info, p =>
                    {
                        try { ui.BeginInvoke((Action)(() => { UpdatePct = p; widget.Invalidate(); })); } catch { }
                    });
                }
                catch (Exception e) { err = e.Message; }
                try
                {
                    ui.BeginInvoke((Action)(() =>
                    {
                        if (err != null)
                        {
                            UpdateState = null;
                            Changed();
                            Balloon("업데이트 실패", err + " — 잠시 후 다시 시도하세요.");
                            return;
                        }
                        UpdateState = "installing";
                        Changed();
                        try
                        {
                            Updater.RunInstaller(path);
                            Quit(); // 설치 프로그램이 끝나면 새 버전을 다시 실행한다
                        }
                        catch (Exception e)
                        {
                            UpdateState = null;
                            Changed();
                            Balloon("업데이트 실패", e.Message);
                        }
                    }));
                }
                catch { }
            });
        }

        // ---------- 토큰 갱신 ----------
        public bool AutoRenew { get { return Settings.GetBool("autoRenew", true); } }

        public void StartRenew()
        {
            if (Renewing) return;
            Renewing = true;
            RenewNote = null;
            Mood = new Mood { Face = "mid", Label = "갱신 중", Level = "warn" };
            Message = "통행증을 갱신하는 중… (Claude Code를 창 없이 잠깐 실행)";
            UpdateTray();
            Changed();
            string path = Settings.GetStr("credentialsPath");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                TokenRefresher.Result res;
                try
                {
                    if (DemoMode) { Thread.Sleep(1500); res = new TokenRefresher.Result { Ok = false, Note = "데모 모드" }; }
                    else res = TokenRefresher.Run(path, true);
                }
                catch (Exception e) { res = new TokenRefresher.Result { Ok = false, Note = e.Message }; }
                try { ui.BeginInvoke((Action)(() => OnRenewed(res))); } catch { }
            });
        }

        void OnRenewed(TokenRefresher.Result res)
        {
            Renewing = false;
            if (res.Ok)
            {
                RenewNote = null;
                Message = "통행증이 갱신되었다! 상태를 다시 확인한다.";
                Changed();
                Busy = false;
                Refresh(false);
                return;
            }
            RenewNote = res.Note;
            Mood = new Mood { Face = "tired", Label = "재접속 필요", Level = "warn" };
            Message = "통행증 갱신에 실패했다… (" + res.Note + ")";
            UpdateTray();
            Changed();
        }

        void Schedule(int ms)
        {
            refreshTimer.Stop();
            refreshTimer.Interval = ms;
            refreshTimer.Start();
            NextAt = DateTime.UtcNow.AddMilliseconds(ms);
        }

        void Changed()
        {
            widget.DataChanged();
            if (strip != null && !strip.IsDisposed) strip.DataChanged();
        }

        // ---------- 트레이 ----------
        void SetTrayFace(string face)
        {
            if (face == trayFace) return;
            trayFace = face;
            try
            {
                var old = tray.Icon;
                int size = SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
                tray.Icon = Sprite.MakeIcon(face, size);
                if (old != null) old.Dispose();
            }
            catch { }
        }

        void UpdateTray()
        {
            SetTrayFace(Mood != null ? Mood.Face : "mid");
            string t = "Usage Quest";
            if (Last != null)
            {
                var hp = Last.Get("five_hour"); var wk = Last.Get("seven_day");
                if (Mood != null) t += " · " + Mood.Label;
                if (hp != null) t += "\nHP " + (int)Math.Round(hp.Remaining) + "% 남음";
                if (wk != null) t += " · STA " + (int)Math.Round(wk.Used) + "% 사용";
            }
            else if (Mood != null) t += " · " + Mood.Label;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; // NotifyIcon 제한 63자
        }

        void BuildMenu()
        {
            menu.Items.Clear();
            var head = new ToolStripMenuItem(tray.Text.Replace("\n", " · ")) { Enabled = false };
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(widget.Visible ? "위젯 숨기기" : "위젯 보이기", null, (a, b) => ToggleWidget()));
            menu.Items.Add(new ToolStripMenuItem("다시 확인", null, (a, b) => Refresh(true)) { Enabled = !Busy && CooldownLeft <= 0 });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("전체 보기", null, (a, b) => SetCompact(!widget.Compact)) { Checked = !widget.Compact });
            menu.Items.Add(new ToolStripMenuItem("항상 위에 표시", null, (a, b) => TogglePin()) { Checked = widget.TopMost });
            menu.Items.Add(new ToolStripMenuItem("효과음", null, (a, b) => ToggleSound()) { Checked = Settings.GetBool("sound", false) });
            menu.Items.Add(new ToolStripMenuItem("토큰 만료 시 자동 갱신", null, (a, b) => Settings.SetBool("autoRenew", !AutoRenew)) { Checked = AutoRenew });
            menu.Items.Add(new ToolStripMenuItem("위젯 위치 초기화 (오른쪽 위)", null, (a, b) => { widget.ResetPosition(); ShowWidget(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("작업표시줄에 사용량 표시", null, (a, b) => SetStripEnabled(!StripEnabled)) { Checked = StripEnabled });
            if (StripEnabled && Native.IsWindows)
            {
                menu.Items.Add(new ToolStripMenuItem("작업표시줄 안에 붙이기 (안 가려짐)", null, (a, b) => { Settings.SetBool("stripEmbed", !EmbedWanted); RecreateStrip(); }) { Checked = EmbedWanted });
                if (EmbedWanted && strip != null && !strip.Embedded && strip.EmbedError != null)
                    menu.Items.Add(new ToolStripMenuItem("   ↳ 붙이기 실패, 겹쳐 띄우는 중 (" + strip.EmbedError + ")") { Enabled = false });
            }
            if (StripEnabled) menu.Items.Add(new ToolStripMenuItem("작업표시줄 칸 위치 초기화", null, (a, b) => { if (strip != null) strip.ResetPosition(); }));
            if (Native.IsWindows)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("컴퓨터 시작 시 자동 실행", null, (a, b) => SetAutoStart(!AutoStartEnabled)) { Checked = AutoStartEnabled });
            }
            menu.Items.Add(new ToolStripSeparator());
            if (Update != null)
                menu.Items.Add(new ToolStripMenuItem(UpdateState == "downloading" ? "업데이트 받는 중… " + UpdatePct + "%" : "업데이트 설치 (" + Updater.Short(Update.Version) + ")", null, (a, b) => StartUpdate()) { Enabled = UpdateState == null, Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(new ToolStripMenuItem("업데이트 확인 (현재 " + Updater.Short(Updater.Current) + ")", null, (a, b) => CheckUpdate(true)) { Enabled = UpdateState == null });
            menu.Items.Add(new ToolStripMenuItem("시작할 때 업데이트 확인", null, (a, b) => Settings.SetBool("autoUpdateCheck", !AutoUpdateCheck)) { Checked = AutoUpdateCheck });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("종료", null, (a, b) => Quit()));
        }

        public void ShowMenuAt(Point screen)
        {
            BuildMenu();
            menu.Show(screen);
        }

        // ---------- 위젯 ----------
        public void ShowWidget()
        {
            widget.KeepOnScreen();
            if (!widget.Visible) widget.Show();
            widget.Activate();
            Settings.SetBool("hidden", false);
        }

        public void HideWidget()
        {
            widget.Hide();
            Settings.SetBool("hidden", true);
            if (!Settings.GetBool("hideTipShown", false) && Native.IsWindows)
            {
                try { tray.ShowBalloonTip(4000, "Usage Quest", "알림 영역(시계 옆)에서 계속 실행 중입니다. 아이콘을 클릭하면 다시 열립니다.", ToolTipIcon.Info); } catch { }
                Settings.SetBool("hideTipShown", true);
            }
        }

        public void ToggleWidget() { if (widget.Visible) HideWidget(); else ShowWidget(); }

        public void SetCompact(bool c) { widget.SetCompact(c); }

        public void TogglePin()
        {
            widget.TopMost = !widget.TopMost;
            Settings.SetBool("pinned", widget.TopMost);
            widget.Invalidate();
        }

        public void ToggleSound()
        {
            Settings.SetBool("sound", !Settings.GetBool("sound", false));
            Sfx.Force();
            widget.Invalidate();
        }

        public void PickCredentials()
        {
            using (var d = new OpenFileDialog { Title = ".credentials.json 파일 선택", Filter = "JSON (*.json)|*.json|모든 파일|*.*" })
            {
                if (d.ShowDialog(widget) == DialogResult.OK)
                {
                    Settings.SetStr("credentialsPath", d.FileName);
                    lastManual = DateTime.MinValue;
                    Refresh(true);
                }
            }
        }

        // ---------- 작업표시줄 칸 ----------
        bool StripEnabled { get { return Settings.GetBool("strip", true); } }
        bool EmbedWanted { get { return Settings.GetBool("stripEmbed", true); } }

        void CreateStrip()
        {
            if (strip != null) return;
            strip = new StripForm(this);
            strip.Start(EmbedWanted);
        }

        void DestroyStrip()
        {
            if (strip == null) return;
            try { strip.Detach(); strip.Close(); strip.Dispose(); } catch { }
            strip = null;
        }

        void RecreateStrip() { DestroyStrip(); if (StripEnabled) CreateStrip(); }

        void SetStripEnabled(bool on)
        {
            Settings.SetBool("strip", on);
            if (on) CreateStrip(); else DestroyStrip();
        }

        void Watch()
        {
            if (strip != null && strip.TaskbarGone)
            {
                // 탐색기 재시작: 작업표시줄이 다시 생기면 새로 붙인다
                try { strip.Dispose(); } catch { }
                strip = null;
            }
            if (strip == null && StripEnabled && (!EmbedWanted || !Native.IsWindows || Native.Taskbar() != IntPtr.Zero)) CreateStrip();
            if (strip != null && !strip.IsDisposed && !strip.Embedded) strip.Place();
        }

        // ---------- 자동 실행 ----------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        bool AutoStartEnabled
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("UsageQuest") != null; }
                catch { return false; }
            }
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("UsageQuest", "\"" + Application.ExecutablePath + "\" --hidden");
                    else k.DeleteValue("UsageQuest", false);
                }
            }
            catch { }
        }

        // 예전(Electron) 버전이 등록한 자동 실행 항목 정리
        static void RemoveOldElectronAutostart()
        {
            if (!Native.IsWindows) return;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    foreach (var name in k.GetValueNames())
                    {
                        var v = Convert.ToString(k.GetValue(name)) ?? "";
                        if (v.IndexOf("Usage Quest.exe", StringComparison.OrdinalIgnoreCase) >= 0) k.DeleteValue(name, false);
                    }
                }
            }
            catch { }
        }

        // ---------- 종료 ----------
        public void Quit()
        {
            Quitting = true;
            refreshTimer.Stop();
            watchTimer.Stop();
            updateTimer.Stop();
            DestroyStrip();
            tray.Visible = false;
            tray.Dispose();
            widget.Close();
            ExitThread();
        }
    }
}
