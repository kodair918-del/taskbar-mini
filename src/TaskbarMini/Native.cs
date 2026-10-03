using System;
using System.Runtime.InteropServices;

namespace TaskbarMini
{
    /// <summary>Win32 の呼び出し。</summary>
    public static partial class Native
    {
        // ---- 通知領域 ----------------------------------------------------------

        public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;
        public const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_INFO = 0x10,
                         NIF_GUID = 0x20, NIF_REALTIME = 0x40, NIF_SHOWTIP = 0x80;
        public const int NIIF_NONE = 0, NIIF_INFO = 1, NIIF_USER = 4, NIIF_NOSOUND = 0x10, NIIF_LARGE_ICON = 0x20,
                         NIIF_RESPECT_QUIET_TIME = 0x80;
        public const int NOTIFYICON_VERSION_4 = 4;

        public const int WM_USER = 0x0400;
        public const int NIN_SELECT = WM_USER + 0, NIN_KEYSELECT = WM_USER + 1,
                         NIN_BALLOONSHOW = WM_USER + 2, NIN_BALLOONHIDE = WM_USER + 3,
                         NIN_BALLOONTIMEOUT = WM_USER + 4, NIN_BALLOONUSERCLICK = WM_USER + 5,
                         NIN_POPUPOPEN = WM_USER + 6, NIN_POPUPCLOSE = WM_USER + 7;
        public const int WM_CONTEXTMENU = 0x007B, WM_MOUSEMOVE = 0x0200,
                         WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_DPICHANGED = 0x02E0, WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A;
        public const int WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uVersion;            // uTimeout と共用
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA data);

        [DllImport("shell32.dll")]
        public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        // ---- ウィンドウ ----------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string msg);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        /// <summary>マウスをこの四角の中に閉じこめる (空を見る画面)。</summary>
        [DllImport("user32.dll")]
        public static extern bool ClipCursor(ref RECT rect);

        /// <summary>閉じこめをやめる (IntPtr.Zero を渡す)。</summary>
        [DllImport("user32.dll")]
        public static extern bool ClipCursor(IntPtr rect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        public const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        public const int SM_CXSMICON = 49;

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                          SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x00000020,
                         WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        // ---- DWM (Windows 11 の角丸など) ----------------------------------------

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_DONOTROUND = 1;

        // ---- 省電力 ----------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        // ---- 全画面アプリの検出 (ゲーム中は通知を控える) ----------------------------

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        public const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4,
                         QUNS_ACCEPTS_NOTIFICATIONS = 5, QUNS_QUIET_TIME = 6, QUNS_APP = 7;

        /// <summary>全画面のゲームや発表中など、通知を出すべきでないとき true。</summary>
        public static bool UserIsBusy()
        {
            try
            {
                if (SHQueryUserNotificationState(out int st) != 0) return false;
                return st == QUNS_BUSY || st == QUNS_RUNNING_D3D_FULL_SCREEN || st == QUNS_PRESENTATION_MODE || st == QUNS_QUIET_TIME;
            }
            catch { return false; }
        }
    }
}
