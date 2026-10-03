using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using TaskbarMini;

namespace TaskbarMini
{
    public enum MiniZone { Orb, Text }

    public enum MiniStatus { Off, Waiting, Shown, NoRoom }

    /// <summary>ミニ画面の設定 (UI スレッドから渡す)。</summary>
    public sealed class MiniConfig
    {
        public bool Enabled = true;
        public string Monitor = "";         // "" = 自動 (メインのタスクバーを優先)
        public int Dock;                    // 0: 自動 / 1: 左端から Offset / 2: 右端から Offset
        public double Offset;               // DIP
        public bool Compact;                // 1 行だけ
        public bool Animate = true;         // 流れ星の筋・玉のまたたき
        public bool Motion = true;          // クリック・キーの火花 (「動きを控えめに」でオフ)

        public MiniConfig Clone() => (MiniConfig)MemberwiseClone();
    }

    /// <summary>
    /// タスクバーの「中」に埋め込む小さなゲーム画面。
    ///
    /// しくみ: タスクバー (エクスプローラーの窓) の子ウィンドウを作り、ボタンの無いすき間に置く。
    /// Windows 11 は昔の「ツールバー (デスクバンド)」を廃止したので、TrafficMonitor などと同じこの方法しかない。
    ///
    /// 注意: よそのプロセスの窓の子になると、入力の順番待ちがエクスプローラーとつながる。
    /// こちらが固まるとタスクバーも固まるので、専用のスレッドで動かし、重い処理 (ゲームの計算・パネル・
    /// UI Automation) は一切ここでしない。描画は GDI+ で 1 回 0.3ms ほど。
    /// </summary>
    public sealed class MiniScreen : IDisposable
    {
        public const string ClassName = "TaskbarMiniWidget";
        private const string CtlClassName = "TaskbarMiniWidgetCtl";

        // ---- 調整できる値 ----------------------------------------------------------
        private const int CheckMs = 2000;           // タスクバーの形の変化を (安く) 調べる間隔
        private const int RescanSeconds = 60;       // 念のため UI Automation で調べ直す間隔
        private const int MinScanGapMs = 1500;      // 調べ直しは最短でもこの間をあける
        private const int AnimFastMs = 33;          // 光・流れ星の筋 (30fps)
        private const int AnimSlowMs = 140;         // 流れ星の玉のまたたきだけのとき (7fps)
        private const int AnimKeyMs = 50;           // キーの光だけのとき (20fps)。打っている間ずっと描くので控えめに
        private const double EdgeDip = 4;           // タスクバーの端に残す余白
        private const double PadDip = 6;            // ボタンとの間にあける余白
        private const double StreakEvery = 2.6;     // 流れ星の筋が横切る間隔 (秒)
        private const double StreakDur = 0.7;       //   横切るのにかかる秒数
        private const double FlashSec = 0.26, PulseSec = 0.8, SparkSec = 0.45;
        private const double KeySparkSec = 0.55;    // キーの光が玉に届くまで
        private const double KeyTextSec = 1.6;      // 最後にキーを押してから「+X」を出しておく秒数
        private const int KeySparksMax = 14;        // 同時に飛ぶキーの光の数

        private const int MSG_CONTENT = Native.WM_APP + 1, MSG_CONFIG = Native.WM_APP + 2, MSG_SCANNED = Native.WM_APP + 3,
                          MSG_PULSE = Native.WM_APP + 4, MSG_QUIT = Native.WM_APP + 5, MSG_KEYS = Native.WM_APP + 6,
                          MSG_ATTN = Native.WM_APP + 7;
        private static readonly IntPtr TimerCheck = (IntPtr)1, TimerAnim = (IntPtr)2, TimerScan = (IntPtr)3;

        // ---- UI スレッドへの知らせ -----------------------------------------------------

        /// <summary>クリックされた。引数はどこか (玉 / 文字) と、ミニ画面の上辺の中央 (パネルを出す位置)。</summary>
        public event Action<MiniZone, Native.POINT> Clicked;
        /// <summary>右クリック。引数はマウスの位置。</summary>
        public event Action<Native.POINT> MenuRequested;
        /// <summary>ドラッグで動かした。モニター、端 (1 左 / 2 右)、端からの距離 (DIP)。</summary>
        public event Action<string, int, double> Docked;
        public event Action<MiniStatus> StatusChanged;

        public MiniStatus Status { get; private set; } = MiniStatus.Waiting;

        /// <summary>いまの位置 (画面座標、物理ピクセル)。出ていなければ null。</summary>
        public Native.RECT? ScreenRect { get { lock (rectLock) return rect; } }

        // ---- スレッドをまたぐもの ----
        private readonly Dispatcher ui;
        private readonly Thread thread;
        private Thread scanThread;
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private readonly AutoResetEvent scanWake = new AutoResetEvent(false);
        private volatile bool disposed;
        private volatile IntPtr ctl;
        private MiniContent pendingContent;
        private MiniConfig pendingConfig;
        private string pendingPulse;
        private long pendingAttnMs = -1;
        private double attnUntil;                   // この時刻 (clock の秒) までふちを金色に点滅させる
        private const double AttnBlinkSec = 0.45;   // 点滅の速さ (点いている・消えている、それぞれの秒)
        private int pendingKeys, keysPosted;
        private string pendingKeyText;
        private volatile List<TaskbarBar> scanResult;
        private readonly object rectLock = new object();
        private Native.RECT? rect;

        // ---- ここから下はミニ画面のスレッドだけが触る ----
        private static readonly Native.WndProcDelegate StaticProc = StaticWndProc;
        private static MiniScreen current;
        private static bool registered;

        private IntPtr mini;
        private IntPtr cursorSizeWE;
        private int shellHookMsg, taskbarCreatedMsg;
        private MiniConfig config;
        private MiniContent content = new MiniContent();
        private MiniRenderer renderer;
        private MiniLayout layout;
        private readonly MiniVisual vis = new MiniVisual();
        private List<TaskbarBar> bars;
        private TaskbarBar placedBar;
        private int px, py, pw = -1, ph = -1;
        private string lastSignature = "";
        private DateTime lastScanAt = DateTime.MinValue, lastScanKick = DateTime.MinValue;
        private bool scanTimerOn, animOn;
        private int animInterval;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double lastAnim, starSince;
        private bool tracking, down, dragging;
        private bool placeLater;                    // ドラッグ中に置き直しを頼まれた (離したときにする)
        private int noRoomStreak;                   // 続けて「空きなし」になった回数
        private Native.POINT downPt;
        private MiniZone downZone;
        private int dragStartX;
        private readonly Random rng = new Random();
        private int errors;
        private readonly HashSet<string> logged = new HashSet<string>();

        // DIB (描いた絵をそのまま窓にするための画像)
        private IntPtr memDC, dib, oldBmp, dibBits;
        private int dibW, dibH;

        private static readonly bool Diag = Environment.GetEnvironmentVariable("TC_DIAG") == "1";

        public MiniScreen(Dispatcher ui, MiniConfig cfg)
        {
            this.ui = ui;
            config = cfg.Clone();
            current = this;
            thread = new Thread(Run) { IsBackground = true, Name = "TaskbarMini" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(3000);
            scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "TaskbarScan" };
            scanThread.Start();
        }

        // ================================================================
        //  UI スレッドから呼ぶもの (どれもすぐ戻る)
        // ================================================================

        public void Update(MiniContent c)
        {
            if (c == null || disposed) return;
            if (Interlocked.Exchange(ref pendingContent, c) == null) Post(MSG_CONTENT);
        }

        public void Configure(MiniConfig cfg)
        {
            if (cfg == null || disposed) return;
            Interlocked.Exchange(ref pendingConfig, cfg.Clone());
            Post(MSG_CONFIG);
        }

        /// <summary>玉をクリックしたときの「+1.2K」。</summary>
        public void Pulse(string text)
        {
            if (disposed) return;
            Interlocked.Exchange(ref pendingPulse, text ?? "");
            Post(MSG_PULSE);
        }

        /// <summary>ふちを金色に点滅させる (チュートリアルの「ここだよ」)。</summary>
        public void Attention(double seconds)
        {
            if (disposed) return;
            Interlocked.Exchange(ref pendingAttnMs, (long)(Math.Max(0, seconds) * 1000));
            Post(MSG_ATTN);
        }

        /// <summary>キーが n 回押された (text はここまでの合計 "+1.2M")。</summary>
        public void KeyPulse(int n, string text)
        {
            if (disposed || n <= 0) return;
            Interlocked.Add(ref pendingKeys, n);
            Volatile.Write(ref pendingKeyText, text);
            if (Interlocked.Exchange(ref keysPosted, 1) == 0) Post(MSG_KEYS);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            scanWake.Set();
            Post(MSG_QUIT);
            thread.Join(1500);
        }

        private void Post(int msg)
        {
            var h = ctl;
            if (h != IntPtr.Zero) Native.PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero);
        }

        private void ToUi(Action a)
        {
            try { ui.BeginInvoke(a); } catch { }
        }

        // ================================================================
        //  ミニ画面のスレッド
        // ================================================================

        private void Run()
        {
            try
            {
                IntPtr inst = Native.GetModuleHandle(null);
                RegisterClasses(inst);
                ctl = Native.CreateWindowEx(Native.WS_EX_TOOLWINDOW, CtlClassName, "Taskbar Mini", Native.WS_POPUP,
                                            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (ctl == IntPtr.Zero) { Log.Write("ミニ画面の窓を作れない: " + Marshal.GetLastWin32Error()); return; }
                shellHookMsg = Native.RegisterWindowMessage("SHELLHOOK");
                taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
                try { Native.RegisterShellHookWindow(ctl); } catch { }
                cursorSizeWE = Native.LoadCursor(IntPtr.Zero, Native.IDC_SIZEWE);
                renderer = new MiniRenderer();
                ready.Set();

                if (config.Enabled) StartWatching();
                else SetStatus(MiniStatus.Off);

                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex) { Log.Write("ミニ画面のスレッドが止まった: " + ex); }
            finally
            {
                ready.Set();
                try { Cleanup(); } catch { }
            }
        }

        private static void RegisterClasses(IntPtr inst)
        {
            if (registered) return;
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                lpfnWndProc = StaticProc,
                hInstance = inst,
                hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_HAND),
                lpszClassName = ClassName,
            };
            Native.RegisterClassEx(ref wc);
            wc.lpszClassName = CtlClassName;
            wc.hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW);
            Native.RegisterClassEx(ref wc);
            registered = true;
        }

        private static IntPtr StaticWndProc(IntPtr h, int m, IntPtr w, IntPtr l)
        {
            var me = current;
            if (me != null)
            {
                try
                {
                    if (h == me.ctl && me.ctl != IntPtr.Zero) return me.CtlProc(h, m, w, l);
                    if (h == me.mini && me.mini != IntPtr.Zero) return me.MiniProc(h, m, w, l);
                }
                catch (Exception ex) { me.OnError(ex); }
            }
            return Native.DefWindowProc(h, m, w, l);
        }

        private void OnError(Exception ex)
        {
            if (++errors <= 5) Log.Write("ミニ画面のエラー: " + ex);
        }

        private void LogOnce(string key, string msg)
        {
            if (logged.Add(key)) Log.Write(msg);
        }

        private void StartWatching()
        {
            Native.SetTimer(ctl, TimerCheck, CheckMs, IntPtr.Zero);
            SetStatus(MiniStatus.Waiting);
            RequestScan(0);
        }

        // ---- 見えない窓: 知らせを受け取る ----

        private IntPtr CtlProc(IntPtr h, int m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case Native.WM_TIMER:
                    if (w == TimerCheck) Check();
                    else if (w == TimerAnim) AnimTick();
                    else if (w == TimerScan)
                    {
                        Native.KillTimer(ctl, TimerScan);
                        scanTimerOn = false;
                        lastScanKick = DateTime.UtcNow;
                        scanWake.Set();
                    }
                    return IntPtr.Zero;
                case MSG_CONTENT: OnContent(); return IntPtr.Zero;
                case MSG_CONFIG: OnConfig(); return IntPtr.Zero;
                case MSG_SCANNED: OnScanned(); return IntPtr.Zero;
                case MSG_PULSE: OnPulse(); return IntPtr.Zero;
                case MSG_KEYS: OnKeys(); return IntPtr.Zero;
                case MSG_ATTN: OnAttention(); return IntPtr.Zero;
                case MSG_QUIT:
                    DestroyMini();
                    try { Native.DeregisterShellHookWindow(ctl); } catch { }
                    Native.DestroyWindow(ctl);
                    return IntPtr.Zero;
                case Native.WM_DESTROY:
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
                case Native.WM_DISPLAYCHANGE:
                case Native.WM_SETTINGCHANGE:
                case Native.WM_DPICHANGED:
                    if (config.Enabled) RequestScan(600);
                    break;
            }
            if (m != 0 && m == taskbarCreatedMsg)
            {
                // エクスプローラーが立ち上がり直した。前のタスクバーはもう無い
                bars = null;
                DestroyMini();
                if (config.Enabled) RequestScan(800);
                return IntPtr.Zero;
            }
            if (m != 0 && m == shellHookMsg)
            {
                // アプリが開いた・閉じた → タスクバーのボタンが増減する
                int code = (int)((long)w & 0x7FFF);
                if (config.Enabled && (code == Native.HSHELL_WINDOWCREATED || code == Native.HSHELL_WINDOWDESTROYED || code == Native.HSHELL_WINDOWREPLACED))
                    RequestScan(700);
                return IntPtr.Zero;
            }
            return Native.DefWindowProc(h, m, w, l);
        }

        // ---- タスクバーの中の窓: マウス ----

        private IntPtr MiniProc(IntPtr h, int m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case Native.WM_MOUSEACTIVATE:
                    // クリックしても、いま使っているアプリからフォーカスを奪わない
                    return (IntPtr)Native.MA_NOACTIVATE;

                case Native.WM_SETCURSOR:
                    if (dragging) { Native.SetCursor(cursorSizeWE); return (IntPtr)1; }
                    break;

                case Native.WM_MOUSEMOVE:
                    if (!tracking)
                    {
                        var tme = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)), dwFlags = Native.TME_LEAVE, hwndTrack = mini };
                        Native.TrackMouseEvent(ref tme);
                        tracking = true;
                        vis.Hover = true;
                        Render();
                    }
                    if (down) DragMove();
                    return IntPtr.Zero;

                case Native.WM_MOUSELEAVE:
                    tracking = false;
                    if (vis.Hover && !down) { vis.Hover = false; Render(); }
                    return IntPtr.Zero;

                case Native.WM_LBUTTONDOWN:
                    if (layout == null) break;
                    Native.SetCapture(mini);
                    down = true;
                    dragging = false;
                    Native.GetCursorPos(out downPt);
                    downZone = Native.LoWord(l) < layout.OrbZoneRight ? MiniZone.Orb : MiniZone.Text;
                    dragStartX = px;
                    vis.Pressed = true;
                    Render();
                    return IntPtr.Zero;

                case Native.WM_LBUTTONUP:
                {
                    if (!down) return IntPtr.Zero;
                    down = false;
                    bool wasDrag = dragging;
                    dragging = false;
                    vis.Pressed = false;
                    vis.Dragging = false;
                    Native.ReleaseCapture();
                    if (wasDrag) FinishDrag();
                    else if (layout != null)
                    {
                        int x = Native.LoWord(l), y = Native.HiWord(l);
                        if (x >= 0 && y >= 0 && x < layout.W && y < layout.H) OnClick(downZone);
                        else vis.Hover = false;
                    }
                    if (placeLater) Place();
                    Render();
                    return IntPtr.Zero;
                }

                case Native.WM_CAPTURECHANGED:
                    if (down)
                    {
                        down = false;
                        vis.Pressed = false;
                        if (dragging) { dragging = false; vis.Dragging = false; FinishDrag(); }
                        else if (placeLater) Place();
                        Render();
                    }
                    return IntPtr.Zero;

                case Native.WM_RBUTTONDOWN:
                    return IntPtr.Zero;

                case Native.WM_RBUTTONUP:
                {
                    Native.GetCursorPos(out var p);
                    ToUi(() => MenuRequested?.Invoke(p));
                    return IntPtr.Zero;
                }

                case Native.WM_DESTROY:
                    // タスクバーごと消えたとき (エクスプローラーの再起動など) もここに来る
                    mini = IntPtr.Zero;
                    placedBar = null;
                    pw = ph = -1;
                    SetRect(null);
                    return IntPtr.Zero;

                case Native.WM_DPICHANGED_AFTERPARENT:
                    RequestScan(200);
                    return IntPtr.Zero;
            }
            return Native.DefWindowProc(h, m, w, l);
        }

        private void OnClick(MiniZone zone)
        {
            if (content.Star || zone == MiniZone.Orb)
            {
                vis.Flash = 1;
                if (config.Motion) AddSparks(content.Star ? 7 : 4);       // 「動きを控えめに」のときは火花なし
                EnsureAnim();
            }
            var r = ScreenRect;
            if (r == null) return;
            var anchor = new Native.POINT { X = (r.Value.Left + r.Value.Right) / 2, Y = r.Value.Top };
            ToUi(() => Clicked?.Invoke(zone, anchor));
        }

        private void AddSparks(int n)
        {
            if (layout == null) return;
            float s = layout.S;
            float cx = layout.OrbX + layout.OrbD / 2f, cy = layout.OrbY + layout.OrbD / 2f;
            for (int i = 0; i < n; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2;
                double v = (26 + rng.NextDouble() * 22) * s;
                vis.Sparks.Add(new Spark
                {
                    X = cx, Y = cy,
                    VX = (float)(Math.Cos(a) * v), VY = (float)(Math.Sin(a) * v),
                    Life = SparkSec * (0.8 + rng.NextDouble() * 0.4),
                });
            }
            if (vis.Sparks.Count > 16) vis.Sparks.RemoveRange(0, vis.Sparks.Count - 16);
        }

        private void DragMove()
        {
            if (placedBar == null || layout == null || mini == IntPtr.Zero) return;
            Native.GetCursorPos(out var p);
            int dx = p.X - downPt.X;
            if (!dragging)
            {
                int th = Math.Max(4, Native.GetSystemMetrics(Native.SM_CXDRAG));
                if (Math.Abs(dx) < th) return;
                dragging = true;
                vis.Dragging = true;
                vis.Pressed = false;
                Native.SetCursor(cursorSizeWE);
                Render();
            }
            int edge = (int)Math.Round(EdgeDip * placedBar.Scale);
            int nx = Math.Max(edge, Math.Min(placedBar.Rect.Width - layout.W - edge, dragStartX + dx));
            if (nx == px) return;
            px = nx;
            Native.SetWindowPos(mini, IntPtr.Zero, px, py, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            UpdateRect();
        }

        private void FinishDrag()
        {
            var bar = placedBar;
            if (bar == null || layout == null) return;
            double s = bar.Scale;
            int dock;
            double off;
            if (px + layout.W / 2 < bar.Rect.Width / 2) { dock = 1; off = px / s; }
            else { dock = 2; off = (bar.Rect.Width - (px + layout.W)) / s; }
            config.Monitor = bar.Device;
            config.Dock = dock;
            config.Offset = off;
            if (Diag) Log.Write($"[diag] mini drag end px={px} w={layout.W} dock={dock} off={off:0.0}");
            string dev = bar.Device;
            ToUi(() => Docked?.Invoke(dev, dock, off));
            Place();        // ボタンに重ねて置いたら、近くの空きへずらす
        }

        // ---- 中身・設定の受け取り ----

        private void OnContent()
        {
            var c = Interlocked.Exchange(ref pendingContent, null);
            if (c == null) return;
            bool starChanged = c.Star != content.Star;
            content = c;
            if (starChanged)
            {
                starSince = clock.Elapsed.TotalSeconds;
                vis.Streak = -1;
                vis.OrbFrame = -1;
                if (c.Star && config.Animate) EnsureAnim();
            }
            if (placedBar == null || layout == null) return;
            if (down) { Render(); return; }             // ドラッグ中は大きさを変えない

            // 幅が変わるなら置き直す (覚えている空きの情報で。UI Automation は使わない)
            var L = renderer.Measure(content, layout.S, layout.H, layout.Compact, layout.OrbOnly);
            int shrink = (int)Math.Round(12 * layout.S);
            bool degraded = layout.OrbOnly || (layout.Compact && !config.Compact);
            if (L.W > layout.W || L.W < layout.W - shrink || degraded)
            {
                // 1 行・玉だけに落ちていたら、広い表示に戻れるかも毎回たしかめる (覚えている空きで計算するだけなので軽い)
                if (L.W != layout.W || degraded) { Place(); return; }
            }
            L.W = layout.W;
            L.TextRight = layout.TextRight;
            layout = L;
            Render();
        }

        private void OnConfig()
        {
            var c = Interlocked.Exchange(ref pendingConfig, null);
            if (c == null) return;
            bool was = config.Enabled;
            config = c;
            if (!config.Enabled)
            {
                Native.KillTimer(ctl, TimerCheck);
                StopAnim();
                DestroyMini();
                SetStatus(MiniStatus.Off);
                return;
            }
            if (!config.Animate) { vis.Streak = -1; vis.OrbFrame = -1; }
            if (!was) { StartWatching(); return; }
            Place();
            if (content.Star && config.Animate) EnsureAnim();
        }

        private void OnKeys()
        {
            Interlocked.Exchange(ref keysPosted, 0);
            int n = Interlocked.Exchange(ref pendingKeys, 0);
            string text = Volatile.Read(ref pendingKeyText);
            if (n <= 0 || mini == IntPtr.Zero || layout == null) return;
            vis.KeyText = text;
            vis.KeyGlow = 1;
            if (config.Motion)
            {
                for (int i = 0; i < Math.Min(n, 4); i++)
                {
                    if (vis.KeySparks.Count >= KeySparksMax) vis.KeySparks.RemoveAt(0);
                    vis.KeySparks.Add(new KeySpark
                    {
                        X0 = layout.OrbOnly ? (float)(rng.NextDouble() * layout.W)
                                            : (float)(layout.TextX + rng.NextDouble() * Math.Max(1, layout.TextRight - layout.TextX)),
                        Y0 = layout.H + 2 * layout.S,
                        Curve = (float)(0.4 + rng.NextDouble() * 0.6),
                        Life = KeySparkSec * (0.85 + rng.NextDouble() * 0.3),
                        Age = -i * 0.06,
                    });
                }
            }
            if (!animOn) { lastAnim = clock.Elapsed.TotalSeconds; SetAnimInterval(AnimKeyMs); }
            Render();
        }

        private void OnAttention()
        {
            long ms = Interlocked.Exchange(ref pendingAttnMs, -1);
            if (ms < 0) return;
            attnUntil = clock.Elapsed.TotalSeconds + ms / 1000.0;
            EnsureAnim();
            Render();
        }

        private void OnPulse()
        {
            var t = Interlocked.Exchange(ref pendingPulse, null);
            if (t == null) return;
            vis.PulseText = t.Length > 0 ? t : null;
            vis.Pulse = 1;
            EnsureAnim();
            Render();
        }

        // ================================================================
        //  タスクバーを調べる・置く
        // ================================================================

        private void RequestScan(int delayMs)
        {
            if (scanTimerOn || ctl == IntPtr.Zero) return;
            double since = (DateTime.UtcNow - lastScanKick).TotalMilliseconds;
            int d = Math.Max(delayMs, (int)(MinScanGapMs - since));
            Native.SetTimer(ctl, TimerScan, Math.Max(1, d), IntPtr.Zero);
            scanTimerOn = true;
        }

        /// <summary>別のスレッド: UI Automation でボタンの位置を調べる。</summary>
        private void ScanLoop()
        {
            while (!disposed)
            {
                scanWake.WaitOne();
                if (disposed) break;
                var sw = Stopwatch.StartNew();
                List<TaskbarBar> found = null;
                try
                {
                    found = TaskbarScan.FindBars();
                    foreach (var b in found)
                    {
                        if (!b.Horizontal) continue;
                        try { b.Busy = TaskbarScan.ScanBusy(b); }
                        catch (Exception ex) { LogOnceSafe("scan", "タスクバーのボタンを調べられない: " + ex.Message); }
                    }
                }
                catch (Exception ex) { LogOnceSafe("find", "タスクバーを探せない: " + ex.Message); }
                if (Diag) Log.Write($"[diag] taskbar scan {sw.ElapsedMilliseconds}ms bars={found?.Count}");
                scanResult = found;
                Post(MSG_SCANNED);
            }
        }

        private readonly object logGate = new object();
        private void LogOnceSafe(string key, string msg)
        {
            lock (logGate) { if (!logged.Add("s:" + key)) return; }
            Log.Write(msg);
        }

        private void OnScanned()
        {
            var found = scanResult;
            if (found == null) return;
            bars = found;
            lastScanAt = DateTime.UtcNow;
            lastSignature = Signature(found);
            if (config.Enabled) Place();
        }

        private static string Signature(List<TaskbarBar> list)
        {
            var parts = new string[list.Count];
            for (int i = 0; i < list.Count; i++) parts[i] = list[i].Signature;
            return string.Join(";", parts);
        }

        /// <summary>2 秒ごと。安い呼び出しだけで、タスクバーが変わったか・窓が無事かを見る。</summary>
        private void Check()
        {
            if (!config.Enabled) return;
            string sig = Signature(TaskbarScan.FindBars());
            if (sig != lastSignature)
            {
                lastSignature = sig;
                RequestScan(300);
            }
            else if ((DateTime.UtcNow - lastScanAt).TotalSeconds > RescanSeconds) RequestScan(0);

            if (mini != IntPtr.Zero && !Native.IsWindow(mini))
            {
                mini = IntPtr.Zero;
                placedBar = null;
                pw = ph = -1;
                SetRect(null);
                RequestScan(0);
            }
            // エクスプローラーが自分の部品を前に出したら、こちらを手前に戻す (ほかのアプリの窓とは取り合わない)
            if (mini != IntPtr.Zero && placedBar != null)
            {
                bool explorerAbove = false;
                IntPtr w = Native.GetWindow(placedBar.Hwnd, Native.GW_CHILD);
                for (int guard = 0; w != IntPtr.Zero && w != mini && guard < 64; guard++, w = Native.GetWindow(w, Native.GW_HWNDNEXT))
                {
                    Native.GetWindowThreadProcessId(w, out uint pid);
                    if (pid == placedBar.Pid) { explorerAbove = true; break; }
                }
                if (explorerAbove)
                    Native.SetWindowPos(mini, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        private IEnumerable<TaskbarBar> Candidates()
        {
            // モニターを決めてあればそこだけ (いまつながっていなければ自動と同じ)
            if (!string.IsNullOrEmpty(config.Monitor))
            {
                var chosen = bars.Find(b => b.Device == config.Monitor);
                if (chosen != null) { yield return chosen; yield break; }
            }
            foreach (var b in bars) yield return b;       // メインが先頭
        }

        private void Place()
        {
            if (down) { placeLater = true; return; }        // ドラッグ中に勝手に動かさない
            placeLater = false;
            if (!config.Enabled) { DestroyMini(); SetStatus(MiniStatus.Off); return; }
            if (bars == null || bars.Count == 0) { DestroyMini(); SetStatus(MiniStatus.Waiting); return; }

            bool anyScanned = false;
            foreach (var bar in Candidates())
            {
                if (!bar.Horizontal || bar.Busy == null) continue;
                anyScanned = true;
                float s = (float)bar.Scale;
                int h = (int)Math.Round(Math.Min(MiniRenderer.HeightDip * s, bar.Rect.Height - 8 * s));
                if (h < 20 * s) continue;       // とても細いタスクバー
                bool here = !string.IsNullOrEmpty(config.Monitor) && bar.Device == config.Monitor;
                int dock = here ? config.Dock : 0;
                int offset = (int)Math.Round(config.Offset * s);
                var gaps = MiniPlacement.Gaps(bar.Busy, bar.Rect.Width, (int)Math.Round(EdgeDip * s), (int)Math.Round(PadDip * s));
                // ふつう (2 行) → 1 行 → 玉だけ、の順に入るものを探す
                int[] modes = config.Compact || h < 32 * s ? new[] { 1, 2 } : new[] { 0, 1, 2 };
                foreach (int mode in modes)
                {
                    bool compact = mode >= 1;
                    var L = renderer.Measure(content, s, h, compact, mode == 2);
                    int? x = MiniPlacement.Place(gaps, bar.Rect.Width, L.W, dock, offset);
                    if (x == null) continue;
                    if (Diag && (x.Value != px || L.W != pw)) Log.Write($"[diag] mini place x={x.Value} w={L.W} mode={mode} dock={dock} off={offset} gaps={string.Join(",", gaps)}");
                    Show(bar, x.Value, (bar.Rect.Height - h) / 2, L);
                    noRoomStreak = 0;
                    SetStatus(MiniStatus.Shown);
                    return;
                }
                if (Diag) Log.Write($"[diag] mini no room on {bar.Device}: busy={string.Join(",", bar.Busy)} gaps={string.Join(",", gaps)}");
            }
            // タスクバーのボタンが動いている途中 (アニメ中) だと、一瞬だけ空きがなく見えることがある。
            // 出ている間は 1 回目は見送り、少し待って調べ直しても空きがないときだけ隠す
            if (anyScanned && mini != IntPtr.Zero && noRoomStreak == 0)
            {
                noRoomStreak = 1;
                RequestScan(MinScanGapMs);
                return;
            }
            noRoomStreak = 0;
            DestroyMini();
            SetStatus(anyScanned ? MiniStatus.NoRoom : MiniStatus.Waiting);
        }

        private void Show(TaskbarBar bar, int x, int y, MiniLayout L)
        {
            if (mini != IntPtr.Zero && (placedBar == null || placedBar.Hwnd != bar.Hwnd || !Native.IsWindow(mini))) DestroyMini();
            layout = L;
            bool created = false;
            if (mini == IntPtr.Zero)
            {
                IntPtr h = Native.CreateWindowEx(Native.WS_EX_LAYERED, ClassName, "Taskbar Mini", Native.WS_CHILD | Native.WS_CLIPSIBLINGS,
                                                 x, y, L.W, L.H, bar.Hwnd, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
                if (h == IntPtr.Zero)
                {
                    LogOnce("create", "ミニ画面をタスクバーに置けない: " + Marshal.GetLastWin32Error());
                    return;
                }
                mini = h;
                created = true;
                pw = ph = -1;
                Log.Write($"ミニ画面をタスクバーに置いた ({(bar.Primary ? "メイン" : bar.Device)} x={x} w={L.W})");
            }
            placedBar = bar;
            bool moved = x != px || y != py || L.W != pw || L.H != ph;
            px = x; py = y; pw = L.W; ph = L.H;
            Render();
            if (created)
            {
                Native.SetWindowPos(mini, Native.HWND_TOP, px, py, pw, ph, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                if (content.Star && config.Animate) EnsureAnim();
            }
            else if (moved)
                Native.SetWindowPos(mini, IntPtr.Zero, px, py, pw, ph, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            UpdateRect();
        }

        private void DestroyMini()
        {
            if (mini == IntPtr.Zero) return;
            var h = mini;
            mini = IntPtr.Zero;
            placedBar = null;
            pw = ph = -1;
            tracking = down = dragging = false;
            vis.Hover = vis.Pressed = vis.Dragging = false;
            if (Native.IsWindow(h)) Native.DestroyWindow(h);
            SetRect(null);
        }

        private void UpdateRect()
        {
            if (mini != IntPtr.Zero && Native.GetWindowRect(mini, out var r)) SetRect(r);
            else SetRect(null);
        }

        private void SetRect(Native.RECT? r)
        {
            lock (rectLock) rect = r;
        }

        private void SetStatus(MiniStatus st)
        {
            if (Status == st) return;
            Status = st;
            if (st == MiniStatus.NoRoom) Log.Write("ミニ画面: タスクバーに空きがないので隠す");
            ToUi(() => StatusChanged?.Invoke(st));
        }

        // ================================================================
        //  描く
        // ================================================================

        private long diagRenders, diagTicks, diagUlw;
        private double diagSince;

        private void Render()
        {
            if (mini == IntPtr.Zero || layout == null) return;
            EnsureDib(layout.W, layout.H);
            if (dibBits == IntPtr.Zero) return;
            long t0 = Diag ? Stopwatch.GetTimestamp() : 0;
            using (var bmp = new Bitmap(layout.W, layout.H, layout.W * 4, PixelFormat.Format32bppPArgb, dibBits))
            using (var g = Graphics.FromImage(bmp))
                renderer.Draw(g, layout, content, vis);
            long t1 = Diag ? Stopwatch.GetTimestamp() : 0;

            var size = new Native.SIZE { cx = layout.W, cy = layout.H };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            if (!Native.UpdateLayeredWindow(mini, IntPtr.Zero, IntPtr.Zero, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA))
                LogOnce("ulw", "ミニ画面を描けない (UpdateLayeredWindow): " + Marshal.GetLastWin32Error());
            if (Diag)
            {
                // 描く時間の平均 (TC_DIAG=1 のとき 10 秒ごとにログ)
                diagRenders++;
                diagTicks += t1 - t0;
                diagUlw += Stopwatch.GetTimestamp() - t1;
                double now = clock.Elapsed.TotalSeconds;
                if (now - diagSince >= 10)
                {
                    double f = 1000.0 / Stopwatch.Frequency / Math.Max(1, diagRenders);
                    Log.Write($"[diag] mini renders={diagRenders} draw={diagTicks * f:0.000}ms ulw={diagUlw * f:0.000}ms (avg)");
                    diagRenders = diagTicks = diagUlw = 0;
                    diagSince = now;
                }
            }
        }

        private void EnsureDib(int w, int h)
        {
            if (dib != IntPtr.Zero && w == dibW && h == dibH) return;
            if (memDC == IntPtr.Zero)
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                memDC = Native.CreateCompatibleDC(screen);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
            FreeDib();
            var bi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dib = Native.CreateDIBSection(memDC, ref bi, 0, out dibBits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { dibBits = IntPtr.Zero; return; }
            oldBmp = Native.SelectObject(memDC, dib);
            dibW = w;
            dibH = h;
        }

        private void FreeDib()
        {
            if (dib == IntPtr.Zero) return;
            Native.SelectObject(memDC, oldBmp);
            Native.DeleteObject(dib);
            dib = IntPtr.Zero;
            dibBits = IntPtr.Zero;
        }

        // ---- アニメ ----

        private void EnsureAnim()
        {
            if (ctl == IntPtr.Zero) return;
            if (!animOn) lastAnim = clock.Elapsed.TotalSeconds;
            SetAnimInterval(AnimFastMs);
        }

        private void SetAnimInterval(int ms)
        {
            if (animOn && animInterval == ms) return;
            Native.SetTimer(ctl, TimerAnim, ms, IntPtr.Zero);
            animOn = true;
            animInterval = ms;
        }

        private void StopAnim()
        {
            if (!animOn) return;
            Native.KillTimer(ctl, TimerAnim);
            animOn = false;
        }

        private void AnimTick()
        {
            if (mini == IntPtr.Zero) { StopAnim(); return; }      // 隠れている間は動かさない
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.5, Math.Max(0, now - lastAnim));
            lastAnim = now;
            bool fast = false;                      // クリック・流れ星の筋 (30fps)
            bool keyAnim = false;                   // キーの光だけ (20fps)
            int wait = int.MaxValue;                // 急がないときの次のコマまで (ms)

            for (int i = vis.KeySparks.Count - 1; i >= 0; i--)
            {
                var ks = vis.KeySparks[i];
                ks.Age += dt;
                if (ks.Age >= ks.Life)
                {
                    vis.KeySparks.RemoveAt(i);
                    vis.Flash = Math.Max(vis.Flash, 0.22);      // 玉に届いたら、ちかっと光る
                    keyAnim = true;
                    continue;
                }
                vis.KeySparks[i] = ks;
                keyAnim = true;
            }
            if (vis.KeyGlow > 0)
            {
                vis.KeyGlow = Math.Max(0, vis.KeyGlow - dt / KeyTextSec);
                if (vis.KeyGlow <= 0) vis.KeyText = null;
                else if (vis.KeyGlow <= MiniRenderer.KeyFade) keyAnim = true;        // 薄くなっていく間は細かく
                else wait = Math.Min(wait, (int)((vis.KeyGlow - MiniRenderer.KeyFade) * KeyTextSec * 1000) + 1);
            }

            if (vis.Flash > 0)
            {
                // クリックの強い光は 30fps、キーが届いたときの弱い光は 20fps で十分
                if (vis.Flash > 0.3) fast = true; else keyAnim = true;
                vis.Flash = Math.Max(0, vis.Flash - dt / FlashSec);
            }
            if (vis.Pulse > 0)
            {
                vis.Pulse = Math.Max(0, vis.Pulse - dt / PulseSec);
                if (vis.Pulse <= 0) vis.PulseText = null;
                fast = true;
            }
            for (int i = vis.Sparks.Count - 1; i >= 0; i--)
            {
                var sp = vis.Sparks[i];
                sp.Age += dt;
                if (sp.Age >= sp.Life) { vis.Sparks.RemoveAt(i); continue; }
                sp.X += (float)(sp.VX * dt);
                sp.Y += (float)(sp.VY * dt);
                sp.VX *= 0.93f;
                sp.VY *= 0.93f;
                vis.Sparks[i] = sp;
                fast = true;
            }

            if (now < attnUntil)
            {
                double left = attnUntil - now;
                vis.Attention = (int)(left / AttnBlinkSec) % 2 == 0;
                wait = Math.Min(wait, (int)(left % AttnBlinkSec * 1000) + 1);
            }
            else vis.Attention = false;

            bool star = content.Star && config.Animate;
            int next = AnimSlowMs;
            if (star)
            {
                double t = now - starSince;
                double ph = t % StreakEvery;
                vis.Streak = ph < StreakDur ? ph / StreakDur : -1;
                vis.OrbFrame = (int)(t / 0.14) % IconRenderer.StarFrames;
                if (vis.Streak >= 0) fast = true;
                else next = (int)Math.Max(15, Math.Min(AnimSlowMs, (StreakEvery - ph) * 1000));
            }
            else
            {
                vis.Streak = -1;
                vis.OrbFrame = -1;
            }

            Render();
            if (fast) SetAnimInterval(AnimFastMs);
            else if (keyAnim) SetAnimInterval(star ? Math.Min(AnimKeyMs, next) : AnimKeyMs);
            else if (star || wait != int.MaxValue) SetAnimInterval(Math.Max(15, Math.Min(star ? next : int.MaxValue, wait)));
            else StopAnim();
        }

        private void Cleanup()
        {
            StopAnim();
            DestroyMini();
            if (ctl != IntPtr.Zero && Native.IsWindow(ctl)) Native.DestroyWindow(ctl);
            ctl = IntPtr.Zero;
            FreeDib();
            if (memDC != IntPtr.Zero) { Native.DeleteDC(memDC); memDC = IntPtr.Zero; }
            renderer?.Dispose();
            renderer = null;
        }
    }
}
