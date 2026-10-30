using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace UsageQuest
{
    static class Native
    {
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong32(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr h, int i, int v);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
        public const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

        public static long GetStyle(IntPtr h)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_STYLE).ToInt64() : (uint)GetWindowLong32(h, GWL_STYLE);
        }
        public static void SetStyle(IntPtr h, long v)
        {
            v &= 0xFFFFFFFF;
            if (IntPtr.Size == 8) SetWindowLongPtr64(h, GWL_STYLE, new IntPtr(v));
            else SetWindowLong32(h, GWL_STYLE, unchecked((int)(uint)v));
        }

        // 시스템 DPI 인식 (글자가 흐려지지 않게). 우리가 직접 배율을 계산해 그린다.
        public static void DpiAware()
        {
            if (!IsWindows) return;
            try { SetProcessDPIAware(); } catch { }
        }

        // 제목줄 없는 창을 마우스로 끌어 옮기기
        public static void DragWindow(IntPtr h)
        {
            if (!IsWindows) return;
            try { ReleaseCapture(); SendMessage(h, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero); } catch { }
        }

        public static IntPtr Taskbar()
        {
            if (!IsWindows) return IntPtr.Zero;
            try { return FindWindow("Shell_TrayWnd", null); } catch { return IntPtr.Zero; }
        }

        public static Rectangle? WindowRect(IntPtr h)
        {
            RECT r;
            try { if (h != IntPtr.Zero && GetWindowRect(h, out r)) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); } catch { }
            return null;
        }
    }
}
