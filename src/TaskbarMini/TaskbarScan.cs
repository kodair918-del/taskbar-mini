using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TaskbarMini;

namespace TaskbarMini
{
    /// <summary>1 本のタスクバー (メインのモニター、またはほかのモニター)。</summary>
    public sealed class TaskbarBar
    {
        public IntPtr Hwnd;
        public Native.RECT Rect;            // 画面座標 (物理ピクセル)
        public uint Dpi = 96;
        public bool Primary;
        public uint Pid;                    // タスクバーを持っているプロセス (エクスプローラー)
        public string Device = "";          // "\\.\DISPLAY1" など。モニターを覚えておくのに使う
        public string Signature = "";       // 形が変わったかを安く調べるための印
        public List<MiniPlacement.Span> Busy;   // ボタンなどが占めている範囲 (左端から)。まだ調べていなければ null

        public double Scale => Dpi / 96.0;
        public bool Horizontal => Rect.Width > Rect.Height;
    }

    /// <summary>
    /// タスクバーを探し、ボタンが並んでいる範囲を調べる。
    /// Windows 11 のタスクバーの中身 (ボタン) は本物のウィンドウではないので、位置は UI Automation で聞く。
    /// 1 回およそ 50ms (CPU は 10ms ほど) かかるので、形が変わったときと 1 分ごとにだけ、専用のスレッドで呼ぶ。
    /// </summary>
    public static class TaskbarScan
    {
        /// <summary>見えているタスクバーを全部。メインが先頭、残りは左から順。</summary>
        public static List<TaskbarBar> FindBars()
        {
            var list = new List<TaskbarBar>();
            var main = Describe(Native.FindWindow("Shell_TrayWnd", null), true);
            if (main != null) list.Add(main);
            var others = new List<TaskbarBar>();
            IntPtr h = IntPtr.Zero;
            for (int guard = 0; guard < 16; guard++)
            {
                h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null);
                if (h == IntPtr.Zero) break;
                var b = Describe(h, false);
                if (b != null) others.Add(b);
            }
            others.Sort((a, b) => a.Rect.Left.CompareTo(b.Rect.Left));
            list.AddRange(others);
            return list;
        }

        private static TaskbarBar Describe(IntPtr h, bool primary)
        {
            if (h == IntPtr.Zero || !Native.IsWindowVisible(h)) return null;
            if (!Native.GetWindowRect(h, out var r) || r.Width <= 0 || r.Height <= 0) return null;
            uint dpi = 96;
            try { dpi = Native.GetDpiForWindow(h); } catch { }
            if (dpi == 0) dpi = 96;
            string dev = "";
            try
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (Native.GetMonitorInfoEx(Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST), ref mi)) dev = mi.szDevice ?? "";
            }
            catch { }
            Native.GetWindowThreadProcessId(h, out uint pid);
            return new TaskbarBar { Hwnd = h, Rect = r, Dpi = dpi, Primary = primary, Device = dev, Pid = pid, Signature = MakeSignature(h, r, dpi) };
        }

        /// <summary>
        /// 大きさ・拡大率・スタートボタン・通知領域の位置。スタートボタンと通知領域は本物のウィンドウで、
        /// アプリが増えて中央のボタンの列が広がるとスタートボタンが動くので、ボタンの増減もだいたいわかる。
        /// </summary>
        private static string MakeSignature(IntPtr h, Native.RECT r, uint dpi)
        {
            string s = h.ToInt64() + ":" + r + "@" + dpi;
            var start = Native.FindWindowEx(h, IntPtr.Zero, "Start", null);
            if (start != IntPtr.Zero && Native.GetWindowRect(start, out var sr)) s += "|s" + sr;
            var tray = Native.FindWindowEx(h, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr)) s += "|t" + tr;
            return s;
        }

        private static Condition busyCondition;

        /// <summary>ボタンなどが占めている範囲。UI スレッドでは呼ばないこと (ミニ画面の窓にも問い合わせが来るため)。</summary>
        public static List<MiniPlacement.Span> ScanBusy(TaskbarBar bar)
        {
            if (busyCondition == null)
            {
                busyCondition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "TrayNotifyWnd"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "TrayDummySearchControl"));
            }

            var root = AutomationElement.FromHandle(bar.Hwnd);
            var cr = new CacheRequest();
            cr.Add(AutomationElement.BoundingRectangleProperty);
            cr.Add(AutomationElement.ClassNameProperty);
            cr.AutomationElementMode = AutomationElementMode.None;
            cr.TreeFilter = Automation.RawViewCondition;
            AutomationElementCollection found;
            using (cr.Activate()) found = root.FindAll(TreeScope.Descendants, busyCondition);

            var spans = new List<MiniPlacement.Span>();
            int left = bar.Rect.Left, width = bar.Rect.Width;
            foreach (AutomationElement e in found)
            {
                if (e.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string == MiniScreen.ClassName) continue;
                if (!(e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty) is System.Windows.Rect rr)) continue;
                if (rr.IsEmpty || rr.Width < 1 || rr.Height < 1) continue;
                // タスクバーの帯と縦に重なるものだけ
                if (rr.Bottom <= bar.Rect.Top || rr.Top >= bar.Rect.Bottom) continue;
                int l = (int)Math.Floor(rr.Left) - left, r = (int)Math.Ceiling(rr.Right) - left;
                if (r <= 0 || l >= width) continue;
                spans.Add(new MiniPlacement.Span(Math.Max(0, l), Math.Min(width, r)));
            }
            // ほかのアプリがタスクバーに埋め込んだ窓 (モニター表示ツールや、別のアカウント・テストで動いている
            // もう 1 つのこのゲームなど) もふさがっている扱い。エクスプローラー自身と、このプロセスの窓は除く
            uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr c = Native.GetWindow(bar.Hwnd, Native.GW_CHILD);
            for (int guard = 0; c != IntPtr.Zero && guard < 64; guard++, c = Native.GetWindow(c, Native.GW_HWNDNEXT))
            {
                if (!Native.IsWindowVisible(c)) continue;
                Native.GetWindowThreadProcessId(c, out uint pid);
                if (pid == bar.Pid || pid == me) continue;
                if (!Native.GetWindowRect(c, out var wr) || wr.Width <= 0 || wr.Height <= 0) continue;
                if (wr.Bottom <= bar.Rect.Top || wr.Top >= bar.Rect.Bottom) continue;
                int l = wr.Left - left, r = wr.Right - left;
                if (r <= 0 || l >= width) continue;
                spans.Add(new MiniPlacement.Span(Math.Max(0, l), Math.Min(width, r)));
            }
            return MiniPlacement.Merge(spans);
        }
    }
}
