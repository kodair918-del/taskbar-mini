using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TaskbarMini;

namespace TaskbarMini
{
    /// <summary>ミニ画面に出す中身。UI スレッドで作って渡す (渡したあとは書き換えない)。</summary>
    public sealed class MiniContent
    {
        public string Light = "0";          // 1 行目: 光の量
        public string Rate = "";            // 2 行目: 毎秒
        public string Line2;                // 2 行目を差し替える文 (流れ星など)。null ならふつう
        public bool Gold;                   // 2 行目を金色に (流れ星・流星群)
        public IconState Orb;               // 左の玉 (通知領域のアイコンと同じ絵)
        public bool Star;                   // 流れ星が来ている
        public Color BgTop, BgBottom;       // 背景の色 (着せかえ)。A が 0 なら元の紺

        public bool SameAs(MiniContent o) =>
            o != null && o.Light == Light && o.Rate == Rate && o.Line2 == Line2 && o.Gold == Gold &&
            o.Orb.Equals(Orb) && o.Star == Star && o.BgTop == BgTop && o.BgBottom == BgBottom;
    }

    /// <summary>アニメの途中の状態 (ミニ画面のスレッドだけが触る)。</summary>
    internal sealed class MiniVisual
    {
        public bool Hover, Pressed, Dragging;
        public bool Attention;              // ふちが金色に光る (チュートリアル)
        public double Flash;                // 玉が光る 1 → 0
        public double Pulse;                // 数字が金色に光る 1 → 0
        public string PulseText;            // 2 行目に一瞬出す "+1.2K"
        public double Streak = -1;          // 流れ星の筋の位置 0〜1 (-1 = 出ていない)
        public int OrbFrame = -1;
        public readonly List<Spark> Sparks = new List<Spark>();
        public double KeyGlow;              // キーで集めた光の文字 1 → 0
        public string KeyText;              // 2 行目に出す "+1.2M" (続けて押している間の合計)
        public readonly List<KeySpark> KeySparks = new List<KeySpark>();
    }

    /// <summary>キーを押すたびに、下から玉へ吸い込まれる小さな光。</summary>
    internal struct KeySpark
    {
        public float X0, Y0, Curve;
        public double Age, Life;            // Age が負の間はまだ出ていない (まとめて来たときにずらす)
    }

    internal struct Spark
    {
        public float X, Y, VX, VY;
        public double Age, Life;
    }

    /// <summary>大きさと文字の位置 (物理ピクセル)。</summary>
    internal sealed class MiniLayout
    {
        public int W, H;
        public bool Compact;
        public bool OrbOnly;                // 玉だけ (すき間がとても狭いとき)
        public float S;                     // 拡大率 (DPI / 96)
        public int OrbD;
        public float OrbX, OrbY;
        public float TextX, TextRight;
        public float Base1, Base2;          // 1 行目・2 行目のベースライン
        public int OrbZoneRight;            // ここより左のクリックは玉
    }

    /// <summary>
    /// ミニ画面を描く。GDI+ で透明度つきの画像を作り、UpdateLayeredWindow でそのまま窓にする。
    /// 1 回 0.3ms ほど。数字が変わったときとアニメの間だけ描く。
    /// </summary>
    internal sealed class MiniRenderer : IDisposable
    {
        // ---- 大きさ (DIP) --------------------------------------------------------
        public const double HeightDip = 40;         // タスクバーのボタンと同じ高さ
        private const float PadL = 6, PadR = 10, Radius = 7;
        private const float OrbNormal = 28, OrbCompact = 24;
        private const float GapOrbText = 7;
        private const float Font1 = 14.5f, Font1Compact = 15f, Font2 = 10.5f;
        private const float LineGap = 4.5f;
        public const double MinNormal = 88, MinCompact = 64, MaxNormal = 150, MaxCompact = 124;

        // ---- 色 ---------------------------------------------------------------------
        private static readonly Color BgTop = Color.FromArgb(238, 27, 31, 70), BgBottom = Color.FromArgb(238, 12, 15, 38);
        private static readonly Color BgTopHover = Color.FromArgb(242, 38, 44, 96), BgBottomHover = Color.FromArgb(242, 18, 22, 54);
        private static readonly Color BgTopPress = Color.FromArgb(242, 20, 23, 54), BgBottomPress = Color.FromArgb(242, 9, 11, 28);
        private static readonly Color TextMain = Color.FromArgb(255, 255, 250, 238);
        private static readonly Color TextDim = Color.FromArgb(255, 168, 176, 228);
        private static readonly Color Gold = Color.FromArgb(255, 255, 206, 84);
        private static readonly Color KeyCyan = Color.FromArgb(255, 140, 224, 255);   // キーボードの光の色
        public const double KeyFade = 0.25;         // KeyGlow がこれを切ったら文字を薄くしていく

        // 背景の小さな星 (幅・高さに対する割合、明るさ)
        private static readonly (float X, float Y, float R, int A)[] BgStars =
        {
            (0.93f, 0.22f, 0.9f, 90), (0.86f, 0.80f, 0.7f, 70), (0.55f, 0.14f, 0.6f, 55),
            (0.70f, 0.90f, 0.6f, 50), (0.40f, 0.86f, 0.5f, 45),
        };

        private float fontScale = -1;
        private FontSet latin, cjk;
        private readonly Bitmap measureBmp = new Bitmap(1, 1);
        private readonly Graphics measure;
        private readonly Dictionary<IconState, Bitmap> orbCache = new Dictionary<IconState, Bitmap>();
        private int orbCacheSize = -1;

        private sealed class FontSet : IDisposable
        {
            public Font F1, F1Compact, F2;
            public float Asc1, Asc1Compact, Asc2;       // 文字の上端からベースラインまで
            public void Dispose() { F1?.Dispose(); F1Compact?.Dispose(); F2?.Dispose(); }
        }

        public MiniRenderer()
        {
            measure = Graphics.FromImage(measureBmp);
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        // ---- 大きさを決める ------------------------------------------------------

        /// <summary>中身に合わせた幅と、文字の位置。heightPx はミニ画面の高さ。</summary>
        public MiniLayout Measure(MiniContent c, float s, int heightPx, bool compact, bool orbOnly = false)
        {
            EnsureFonts(s);
            if (orbOnly) compact = true;
            var L = new MiniLayout { H = heightPx, S = s, Compact = compact, OrbOnly = orbOnly };
            float h = heightPx;
            L.OrbD = (int)Math.Round(Math.Min((compact ? OrbCompact : OrbNormal) * s, h - 6 * s));
            L.OrbX = (float)Math.Round(PadL * s);
            L.OrbY = (float)Math.Round((h - L.OrbD) / 2);
            L.TextX = L.OrbX + L.OrbD + (compact ? GapOrbText - 1 : GapOrbText) * s;
            L.OrbZoneRight = (int)(L.OrbX + L.OrbD + GapOrbText * s / 2);
            if (orbOnly)
            {
                // 玉だけ: 左右の余白をそろえた小さな四角。全体が玉 (クリックで光)
                L.W = (int)Math.Ceiling(L.OrbX * 2 + L.OrbD);
                L.TextX = L.TextRight = L.W;
                L.OrbZoneRight = L.W;
                return L;
            }

            float w1 = TextWidth(c.Light, true, compact);
            float textW = w1;
            // 幅は光と毎秒の数字だけで決める。流れ星などの差し替えの文は、入らなければ文字を縮めて収める
            // (一時的な文のせいで幅が変わり、すき間に入らなくなるのを防ぐ)
            if (!compact) textW = Math.Max(textW, TextWidth(c.Rate, false, false));
            double wDip = (L.TextX + textW) / s + (compact ? PadR - 1 : PadR);
            wDip = Math.Max(compact ? MinCompact : MinNormal, Math.Min(compact ? MaxCompact : MaxNormal, wDip));
            // 2 DIP 単位に丸めて、数字の桁が 1 つ増えるたびにガタつかないようにする
            L.W = (int)Math.Ceiling(Math.Ceiling(wDip / 2) * 2 * s);
            L.TextRight = L.W - (compact ? PadR - 1 : PadR) * s;

            var fs = latin;
            if (compact)
            {
                float cap = fs.F1Compact.Size * 0.70f;
                L.Base1 = (h + cap) / 2;
            }
            else
            {
                float cap1 = fs.F1.Size * 0.70f, cap2 = fs.F2.Size * 0.70f;
                float block = cap1 + LineGap * s + cap2;
                L.Base1 = (h - block) / 2 + cap1;
                L.Base2 = L.Base1 + LineGap * s + cap2;
            }
            return L;
        }

        private float TextWidth(string text, bool line1, bool compact)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var fs = IsCjk(text) ? cjk : latin;
            var f = line1 ? (compact ? fs.F1Compact : fs.F1) : fs.F2;
            return measure.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        // ---- 描く ---------------------------------------------------------------------

        // ---- 描き置き (毎回描くと重い部分。打鍵中は 1 秒に 30 回描く) ----
        private Bitmap bgCache;
        private string bgKey;
        private Region frameRegion;
        private string regionKey;

        public void Draw(Graphics g, MiniLayout L, MiniContent c, MiniVisual v)
        {
            float s = L.S;
            // 背景 (枠・星雲・背景の星・縁) は描き置きを写すだけ。透明度ごと上書きするので Clear は要らない
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(Background(L, c, v), 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.Bilinear;

            float ocx = L.OrbX + L.OrbD / 2f, ocy = L.OrbY + L.OrbD / 2f;
            g.Clip = FrameRegion(L);

            // 流れ星の筋 (右上から左下へ)
            if (v.Streak >= 0) DrawStreak(g, L, v.Streak);

            // 玉
            var orbState = c.Orb;
            orbState.LightTaskbar = false;
            orbState.StarFrame = c.Star ? Math.Max(0, v.OrbFrame) : -1;
            g.DrawImageUnscaled(OrbBitmap(orbState, L.OrbD), (int)L.OrbX, (int)L.OrbY);

            // クリック・キーが届いたときの光
            if (v.Flash > 0.01)
            {
                float fr = L.OrbD * (0.55f + 0.35f * (float)(1 - v.Flash));
                DrawFlash(g, ocx, ocy, fr, (float)(210 / 255.0 * v.Flash));
            }
            foreach (var sp in v.Sparks)
            {
                double k = 1 - sp.Age / sp.Life;
                if (k <= 0) continue;
                IconRenderer.DrawSparkle(g, sp.X, sp.Y, (float)(6 * s * (0.6 + 0.4 * k)), 1.6f * s,
                                         (float)(sp.Age * 200), Color.FromArgb((int)(230 * k), 255, 226, 140));
            }
            // キーの光: 下のふちから、弧を描いて玉へ吸い込まれる
            foreach (var ks in v.KeySparks)
            {
                if (ks.Age < 0) continue;
                double t = Math.Min(1, ks.Age / ks.Life);
                float u = (float)(t * t * (3 - 2 * t)), iu = 1 - u;
                float mx = (ks.X0 + ocx) / 2, my = Math.Min(ks.Y0, ocy) - ks.Curve * L.H * 0.35f;
                float x = iu * iu * ks.X0 + 2 * iu * u * mx + u * u * ocx;
                float y = iu * iu * ks.Y0 + 2 * iu * u * my + u * u * ocy;
                int a = (int)(235 * Math.Min(1, t / 0.15) * (t > 0.8 ? (1 - t) / 0.2 : 1));
                if (a <= 0) continue;
                float len = (float)(s * (5.5 - 2.5 * t));
                IconRenderer.DrawSparkle(g, x, y, len, len * 0.36f, (float)(t * 90), Color.FromArgb(a, 176, 236, 255));
            }
            g.ResetClip();

            // ---- 文字 (玉だけのときは無し) ----
            if (L.OrbOnly) return;
            g.SetClip(new RectangleF(L.TextX - 1, 0, L.TextRight - L.TextX + 2, L.H));
            Color c1 = v.Pulse > 0.01 ? Mix(TextMain, Gold, v.Pulse) : TextMain;
            DrawText(g, c.Light, true, L.Compact, L.TextX, L.Base1, c1);
            if (!L.Compact)
            {
                string line2 = c.Line2 ?? c.Rate;
                Color c2 = c.Gold ? Gold : TextDim;
                float x2 = L.TextX;
                if (v.PulseText != null && v.Pulse > 0.01 && c.Line2 == null)
                {
                    line2 = v.PulseText;
                    c2 = Mix(TextDim, Gold, Math.Min(1, v.Pulse * 1.6));
                }
                else if (v.KeyText != null && v.KeyGlow > 0.01 && c.Line2 == null)
                {
                    // キーで集めた光: キーボードの印 + 合計 (水色)。最後は薄くなって毎秒の表示に戻る
                    double k = Math.Min(1, v.KeyGlow / KeyFade);
                    line2 = v.KeyText;
                    c2 = Mix(TextDim, KeyCyan, k);
                    DrawKeyboardGlyph(g, L, Color.FromArgb((int)(60 + 195 * k), KeyCyan));
                    x2 += 13.5f * s;
                }
                DrawText(g, line2, false, false, x2, L.Base2, c2, L.TextRight - x2);
            }
            g.ResetClip();
        }

        /// <summary>動かない部分: 枠 (状態で色が変わる)・玉のうしろの星雲・背景の星・縁。変わったときだけ描き直す。</summary>
        private Bitmap Background(MiniLayout L, MiniContent c, MiniVisual v)
        {
            float s = L.S;
            int state = v.Dragging ? 3 : v.Attention ? 4 : v.Pressed ? 2 : v.Hover ? 1 : 0;
            var tc = Palette.TierColor(c.Orb.Tier);
            var tier = c.Star ? Gold : Color.FromArgb(255, tc.R, tc.G, tc.B);
            Color baseTop = c.BgTop.A != 0 ? c.BgTop : BgTop, baseBottom = c.BgBottom.A != 0 ? c.BgBottom : BgBottom;
            string key = L.W + "x" + L.H + "@" + s + "|" + L.OrbD + "|" + L.OrbX + "|" + state + "|" + tier.ToArgb() + "|" + c.Orb.Idle +
                         "|" + baseTop.ToArgb() + "|" + baseBottom.ToArgb();
            if (bgCache != null && key == bgKey) return bgCache;
            bgCache?.Dispose();
            bgKey = key;
            bgCache = new Bitmap(L.W, L.H, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bgCache))
            using (var frame = RoundRect(new RectangleF(0.5f, 0.5f, L.W - 1, L.H - 1), Radius * s))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // 押している間は暗く、マウスが乗っている・ドラッグ中は明るく
                Color top = state == 2 ? Shade(baseTop, 0.72) : state != 0 ? Shade(baseTop, 1.4) : baseTop;
                Color bottom = state == 2 ? Shade(baseBottom, 0.72) : state != 0 ? Shade(baseBottom, 1.4) : baseBottom;
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(0, L.H), top, bottom))
                    g.FillPath(br, frame);

                g.SetClip(frame);
                // 玉のうしろの星雲 (光の段の色。流れ星のときは金)
                float ocx = L.OrbX + L.OrbD / 2f, ocy = L.OrbY + L.OrbD / 2f;
                float nr = L.OrbD * 1.15f;
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(ocx - nr, ocy - nr, nr * 2, nr * 2);
                    using (var br = new PathGradientBrush(path))
                    {
                        br.CenterColor = Color.FromArgb(c.Orb.Idle ? 30 : 64, tier);
                        br.SurroundColors = new[] { Color.FromArgb(0, tier) };
                        g.FillPath(br, path);
                    }
                }
                // 背景の小さな星
                foreach (var st in BgStars)
                {
                    float r = Math.Max(0.6f, st.R * s);
                    using (var br = new SolidBrush(Color.FromArgb(st.A, 220, 226, 255)))
                        g.FillEllipse(br, st.X * L.W - r, st.Y * L.H - r, r * 2, r * 2);
                }
                g.ResetClip();

                // 縁
                int edgeA = state == 4 ? 245 : state == 3 ? 150 : state == 1 ? 70 : 40;
                var edgeCol = state >= 3 ? Color.FromArgb(edgeA, Gold) : Color.FromArgb(edgeA, 255, 255, 255);
                float edgeW = state == 4 ? Math.Max(2f, s * 2.4f) : Math.Max(1f, s * 0.8f);
                using (var pen = new Pen(edgeCol, edgeW)) g.DrawPath(pen, frame);
                using (var pen = new Pen(Color.FromArgb(24, 255, 255, 255), 1f))
                    g.DrawLine(pen, Radius * s, 1.5f, L.W - Radius * s, 1.5f);
            }
            return bgCache;
        }

        /// <summary>角丸の枠の形 (動く光がはみ出さないように切り取る)。</summary>
        private Region FrameRegion(MiniLayout L)
        {
            string key = L.W + "x" + L.H + "@" + L.S;
            if (frameRegion != null && key == regionKey) return frameRegion;
            frameRegion?.Dispose();
            regionKey = key;
            using (var frame = RoundRect(new RectangleF(0.5f, 0.5f, L.W - 1, L.H - 1), Radius * L.S))
                frameRegion = new Region(frame);
            return frameRegion;
        }

        /// <summary>
        /// 玉が光る: 同心円を 3 重に薄く塗って、やわらかい光に見せる。
        /// (ぼかし画像を拡大縮小・半透明で写すと 1 回 0.35ms かかった。これは 0.04ms)
        /// </summary>
        private static void DrawFlash(Graphics g, float cx, float cy, float r, float alpha)
        {
            int a = (int)(Math.Max(0f, Math.Min(1f, alpha)) * 255 / 3);
            if (a <= 0) return;
            using (var br = new SolidBrush(Color.FromArgb(a, 255, 250, 232)))
            {
                g.FillEllipse(br, cx - r, cy - r, r * 2, r * 2);
                float r2 = r * 0.66f, r3 = r * 0.36f;
                g.FillEllipse(br, cx - r2, cy - r2, r2 * 2, r2 * 2);
                g.FillEllipse(br, cx - r3, cy - r3, r3 * 2, r3 * 2);
            }
        }

        private void DrawText(Graphics g, string text, bool line1, bool compact, float x, float baseline, Color col, float maxWidth = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            var fs = IsCjk(text) ? cjk : latin;
            var f = line1 ? (compact ? fs.F1Compact : fs.F1) : fs.F2;
            float asc = line1 ? (compact ? fs.Asc1Compact : fs.Asc1) : fs.Asc2;
            Font small = null;
            if (maxWidth > 0)
            {
                float w = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                if (w > maxWidth)
                {
                    // 入りきらない: 75% まで縮める (それでも入らなければ端が切れる)
                    float k = Math.Max(0.75f, maxWidth / w);
                    small = new Font(f.FontFamily, f.Size * k, f.Style, GraphicsUnit.Pixel);
                    asc *= k;
                    f = small;
                }
            }
            using (small)
            using (var br = new SolidBrush(col))
                g.DrawString(text, f, br, x, baseline - asc, StringFormat.GenericTypographic);
        }

        /// <summary>小さなキーボードの印 (角丸の四角 + キーの点 + スペースキー)。</summary>
        private void DrawKeyboardGlyph(Graphics g, MiniLayout L, Color col)
        {
            float s = L.S;
            float w = 10.5f * s, h = 7f * s;
            float yc = L.Base2 - latin.F2.Size * 0.70f / 2;
            float x = L.TextX, y = yc - h / 2;
            using (var pen = new Pen(col, Math.Max(1f, 0.9f * s)))
            using (var path = RoundRect(new RectangleF(x, y, w, h), 1.6f * s))
                g.DrawPath(pen, path);
            float d = Math.Max(1f, 1.05f * s);
            using (var br = new SolidBrush(col))
            {
                for (int r = 0; r < 2; r++)
                    for (int k = 0; k < 3; k++)
                        g.FillRectangle(br, x + w * (0.24f + 0.26f * k) - d / 2, y + h * (0.30f + 0.22f * r) - d / 2, d, d);
                g.FillRectangle(br, x + w * 0.30f, y + h * 0.76f - d / 2, w * 0.40f, d);
            }
        }

        private static void DrawStreak(Graphics g, MiniLayout L, double p)
        {
            float s = L.S;
            // 右上の外から、玉の右あたりの下へ
            var from = new PointF(L.W + 6 * s, -4 * s);
            var to = new PointF(L.TextX - 4 * s, L.H + 4 * s);
            float hx = (float)(from.X + (to.X - from.X) * p), hy = (float)(from.Y + (to.Y - from.Y) * p);
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float tail = 26 * s;
            var tp = new PointF(hx - dx / len * tail, hy - dy / len * tail);
            int a = (int)(230 * Math.Sin(Math.PI * Math.Min(1, p * 1.15)));
            if (a <= 0) return;
            using (var br = new LinearGradientBrush(tp, new PointF(hx + 0.01f, hy + 0.01f), Color.FromArgb(0, Gold), Color.FromArgb(a, 255, 230, 150)))
            using (var pen = new Pen(br, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, tp, new PointF(hx, hy));
            float hr = 1.5f * s;
            using (var b = new SolidBrush(Color.FromArgb(a, 255, 250, 225))) g.FillEllipse(b, hx - hr, hy - hr, hr * 2, hr * 2);
        }

        // ---- 部品 -------------------------------------------------------------------

        private Bitmap OrbBitmap(IconState st, int size)
        {
            if (size != orbCacheSize || orbCache.Count > 24) ClearOrbs();
            orbCacheSize = size;
            if (!orbCache.TryGetValue(st, out var bmp))
            {
                bmp = IconRenderer.Render(Math.Max(8, size), st);
                orbCache[st] = bmp;
            }
            return bmp;
        }

        private void ClearOrbs()
        {
            foreach (var b in orbCache.Values) b.Dispose();
            orbCache.Clear();
        }

        private void EnsureFonts(float s)
        {
            if (Math.Abs(s - fontScale) < 0.001f && latin != null) return;
            latin?.Dispose();
            cjk?.Dispose();
            fontScale = s;
            latin = MakeFonts(s, new[] { "Segoe UI Semibold", "Segoe UI" }, new[] { "Segoe UI", "Arial" });
            cjk = MakeFonts(s, new[] { "Yu Gothic UI Semibold", "Yu Gothic UI", "Meiryo UI", "MS UI Gothic" },
                               new[] { "Yu Gothic UI", "Meiryo UI", "MS UI Gothic", "Segoe UI" });
        }

        private static FontSet MakeFonts(float s, string[] bold, string[] regular)
        {
            var fs = new FontSet();
            var fb = Family(bold);
            var fr = Family(regular);
            var styleB = fb.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            if (fb.Name == fr.Name && fb.IsStyleAvailable(FontStyle.Bold)) styleB = FontStyle.Bold;   // Semibold が無いとき
            fs.F1 = new Font(fb, Font1 * s, styleB, GraphicsUnit.Pixel);
            fs.F1Compact = new Font(fb, Font1Compact * s, styleB, GraphicsUnit.Pixel);
            fs.F2 = new Font(fr, Font2 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fs.Asc1 = Ascent(fs.F1);
            fs.Asc1Compact = Ascent(fs.F1Compact);
            fs.Asc2 = Ascent(fs.F2);
            return fs;
        }

        private static float Ascent(Font f)
        {
            var fam = f.FontFamily;
            return f.Size * fam.GetCellAscent(f.Style) / fam.GetEmHeight(f.Style);
        }

        private static FontFamily Family(string[] names)
        {
            foreach (var n in names)
            {
                try { return new FontFamily(n); }
                catch (ArgumentException) { }
            }
            return FontFamily.GenericSansSerif;
        }

        private static bool IsCjk(string s)
        {
            foreach (char ch in s) if (ch >= 0x2E80) return true;
            return false;
        }

        private static Color Shade(Color c, double k) => Color.FromArgb(c.A, (int)Math.Min(255, c.R * k + (k > 1 ? 6 : 0)),
                                                                        (int)Math.Min(255, c.G * k + (k > 1 ? 6 : 0)), (int)Math.Min(255, c.B * k + (k > 1 ? 8 : 0)));

        private static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                                  (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            ClearOrbs();
            bgCache?.Dispose();
            frameRegion?.Dispose();
            latin?.Dispose();
            cjk?.Dispose();
            measure.Dispose();
            measureBmp.Dispose();
        }
    }
}
