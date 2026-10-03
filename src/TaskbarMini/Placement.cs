using System;
using System.Windows;
using System.Windows.Interop;
using TaskbarMini;

namespace TaskbarMini
{
    public enum TaskbarEdge { Bottom, Top, Left, Right }

    /// <summary>
    /// 小窓を通知領域の近くに置く。計算は物理ピクセルで行い、
    /// タスクバーがどの辺にあっても、モニターごとの拡大率が違っても正しく置く。
    /// </summary>
    public static class Placement
    {
        public static Native.POINT CursorOr(Native.POINT? anchor)
        {
            if (anchor.HasValue) return anchor.Value;
            Native.GetCursorPos(out var p);
            return p;
        }

        public static void Near(Window w, Native.POINT anchor, double gapDip = 0)
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            IntPtr mon = Native.MonitorFromPoint(anchor, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(mon, ref mi)) return;

            double scale = 1;
            try
            {
                if (Native.GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0;
            }
            catch { }

            // SizeToContent の窓は Width/Height が NaN なので実際の大きさを使う
            double wd = double.IsNaN(w.Width) ? w.ActualWidth : w.Width;
            double hd = double.IsNaN(w.Height) ? w.ActualHeight : w.Height;
            int pw = (int)Math.Round(wd * scale);
            int ph = (int)Math.Round(hd * scale);
            int gap = (int)Math.Round(gapDip * scale);
            var work = mi.rcWork;
            var edge = EdgeOf(mi, anchor);

            int x, y;
            switch (edge)
            {
                case TaskbarEdge.Top:
                    x = Clamp(anchor.X - pw / 2, work.Left, work.Right - pw);
                    y = work.Top + gap;
                    break;
                case TaskbarEdge.Left:
                    x = work.Left + gap;
                    y = Clamp(anchor.Y - ph / 2, work.Top, work.Bottom - ph);
                    break;
                case TaskbarEdge.Right:
                    x = work.Right - pw - gap;
                    y = Clamp(anchor.Y - ph / 2, work.Top, work.Bottom - ph);
                    break;
                default:
                    x = Clamp(anchor.X - pw / 2, work.Left, work.Right - pw);
                    y = work.Bottom - ph - gap;
                    break;
            }
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>画面の作業領域の高さ (DIP)。パネルの高さを合わせるのに使う。</summary>
        public static double WorkHeightDip(Native.POINT anchor)
        {
            IntPtr mon = Native.MonitorFromPoint(anchor, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(mon, ref mi)) return 800;
            double scale = 1;
            try { if (Native.GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0; } catch { }
            return mi.rcWork.Height / scale;
        }

        public static TaskbarEdge EdgeOf(Native.MONITORINFO mi, Native.POINT anchor)
        {
            var m = mi.rcMonitor; var wk = mi.rcWork;
            if (wk.Bottom < m.Bottom) return TaskbarEdge.Bottom;
            if (wk.Top > m.Top) return TaskbarEdge.Top;
            if (wk.Left > m.Left) return TaskbarEdge.Left;
            if (wk.Right < m.Right) return TaskbarEdge.Right;
            // 自動で隠れるタスクバー: アイコンの位置で判断
            int cy = (m.Top + m.Bottom) / 2, cx = (m.Left + m.Right) / 2;
            if (Math.Abs(anchor.Y - cy) >= Math.Abs(anchor.X - cx)) return anchor.Y > cy ? TaskbarEdge.Bottom : TaskbarEdge.Top;
            return anchor.X > cx ? TaskbarEdge.Right : TaskbarEdge.Left;
        }

        private static int Clamp(int v, int lo, int hi) => hi < lo ? lo : v < lo ? lo : v > hi ? hi : v;

        /// <summary>フォーカスを奪わない窓にする (流れ星の吹き出しなど)。</summary>
        public static void MakeNoActivate(Window w)
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        }

        /// <summary>Alt+Tab に出さない。</summary>
        public static void MakeToolWindow(Window w)
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
        }
    }
}
