using System;
using System.Runtime.InteropServices;

namespace TaskbarMini
{
    /// <summary>タスクバーのミニ画面 (自前の Win32 ウィンドウ) で使う呼び出し。</summary>
    public static partial class Native
    {
        public delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam, lParam;
            public int time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        public const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000), WS_CLIPSIBLINGS = 0x04000000;
        public const int CS_DBLCLKS = 0x0008;
        public const int WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021,
                         WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MOUSELEAVE = 0x02A3,
                         WM_CAPTURECHANGED = 0x0215, WM_DPICHANGED_AFTERPARENT = 0x02E3, WM_APP = 0x8000,
                         WM_CANCELMODE = 0x001F;
        public const int MA_NOACTIVATE = 3;
        public const int TME_LEAVE = 0x02;
        public const int GW_CHILD = 5, GW_HWNDNEXT = 2;
        public const int IDC_ARROW = 32512, IDC_HAND = 32649, IDC_SIZEWE = 32644;
        public const int SM_CXDRAG = 68;
        public const uint SWP_NOREDRAW = 0x0008, SWP_HIDEWINDOW = 0x0080;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const int AC_SRC_OVER = 0, AC_SRC_ALPHA = 1, ULW_ALPHA = 2;
        public const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWREPLACED = 13;


        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h,
                                                   IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, int min, int max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, int ms, IntPtr proc);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, IntPtr id);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr GetCapture();
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr inst, int id);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc,
                                                      ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, int usage, out IntPtr bits, IntPtr section, int offset);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        public static int LoWord(IntPtr p) => (short)((long)p & 0xFFFF);
        public static int HiWord(IntPtr p) => (short)(((long)p >> 16) & 0xFFFF);
    }
}
