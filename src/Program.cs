using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Usage Quest")]
[assembly: AssemblyProduct("Usage Quest")]
[assembly: AssemblyDescription("Claude Code /usage 사용량을 게임 화면처럼 보여주는 위젯")]
[assembly: AssemblyVersion("2.5.0.0")]
[assembly: AssemblyFileVersion("2.5.0.0")]

namespace UsageQuest
{
    static class Program
    {
        static void Log(Exception e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Settings.Dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // agy(Antigravity CLI) 상태줄에서 호출될 때: 창 없이 값만 저장하고 바로 끝
            if (args.Contains("--agy-statusline")) { AgyCli.RunStatusline(); return; }

            string demo = null;
            var d = args.FirstOrDefault(a => a.StartsWith("--demo"));
            if (d != null) demo = d.Contains("=") ? d.Split('=')[1] : "fresh";
            bool hidden = args.Contains("--hidden");

            // 한 번만 실행: 이미 켜져 있으면 그 창을 띄우고 끝낸다
            bool created;
            var mutex = new Mutex(true, "UsageQuest.SingleInstance", out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "UsageQuest.Show");
            if (!created && demo == null)
            {
                showEvent.Set();
                return;
            }

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            Native.DpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Px.LoadFonts();

            // 예상 못 한 오류는 기록만 하고 계속 실행 (%APPDATA%\UsageQuest\error.log)
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (a, e) => Log(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (a, e) => Log(e.ExceptionObject as Exception);

            var core = new AppCore(demo, hidden);
            var ui = new Control();
            ui.CreateControl();
            var h = ui.Handle;
            ThreadPool.RegisterWaitForSingleObject(showEvent, (st, to) =>
            {
                try { ui.BeginInvoke((Action)(() => core.ShowWidget())); } catch { }
            }, null, -1, false);

            Application.Run(core);
            GC.KeepAlive(mutex);
        }
    }
}
