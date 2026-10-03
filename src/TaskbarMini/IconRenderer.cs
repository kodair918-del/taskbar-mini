using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TaskbarMini;

namespace TaskbarMini
{
    /// <summary>通知領域アイコンに描く中身。</summary>
    public struct IconState
    {
        public int Tier;            // 光の段 (色)
        public double Progress;     // 段の中の進み具合 0〜1 (リング)
        public bool Idle;           // 何も生産していない
        public bool LightTaskbar;   // タスクバーが明るいテーマ
        public bool Badge;          // 買えるものがある
        public int StarFrame;       // 流れ星が来ているときのアニメのコマ。来ていなければ -1
        public bool Shower;         // 流星群の最中
        public int Prestige;        // 0: まだ超新星なし / 1〜: 超新星の格 (きらめきの強さ)
        public bool BigBang;        // ビッグバンを経験した (外側にもう 1 本の軌道)

        public override bool Equals(object obj) => obj is IconState o &&
            o.Tier == Tier && (int)(o.Progress * 64) == (int)(Progress * 64) && o.Idle == Idle &&
            o.LightTaskbar == LightTaskbar && o.Badge == Badge && o.StarFrame == StarFrame &&
            o.Shower == Shower && o.Prestige == Prestige && o.BigBang == BigBang;

        public override int GetHashCode() => Tier ^ (int)(Progress * 64) << 8 ^ StarFrame << 16;
    }

    /// <summary>
    /// アイコンを 4 倍の大きさで描いてから縮小する。16px でも形が崩れないように、
    /// 文字は使わず「色 = 段」「リング = 進み具合」だけで見せる。
    /// </summary>
    public static class IconRenderer
    {
        private const int Supersample = 4;
        public const int StarFrames = 8;

        public static Bitmap Render(int size, IconState st)
        {
            int W = size * Supersample;
            using (var big = new Bitmap(W, W, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    Draw(g, W, st);
                }
                var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(big, new Rectangle(0, 0, size, size), 0, 0, W, W, GraphicsUnit.Pixel);
                }
                return small;
            }
        }

        private static Color C((byte R, byte G, byte B) c, int a = 255, double k = 1) =>
            Color.FromArgb(a, (int)(c.R * k), (int)(c.G * k), (int)(c.B * k));

        private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        private static void Draw(Graphics g, int W, IconState st)
        {
            float c = W / 2f;
            double dim = st.Idle ? 0.5 : 1.0;
            bool star = st.StarFrame >= 0;

            var tierCol = C(Palette.TierColor(st.Tier), 255, dim);
            // 明るいタスクバーで白っぽい色は沈める
            if (st.LightTaskbar && tierCol.R + tierCol.G + tierCol.B > 660)
                tierCol = Color.FromArgb(255, tierCol.R * 7 / 10, tierCol.G * 7 / 10, tierCol.B * 7 / 10);
            var ringCol = (st.Shower || star) ? C(Palette.Gold) : tierCol;

            // ---- 外周のリング ----
            float margin = W * 0.04f;
            float rw = W * 0.12f;
            var ringRect = new RectangleF(margin + rw / 2, margin + rw / 2, W - 2 * margin - rw, W - 2 * margin - rw);
            var track = st.LightTaskbar ? Color.FromArgb(46, 0, 0, 0) : Color.FromArgb(48, 255, 255, 255);
            using (var p = new Pen(track, rw)) g.DrawEllipse(p, ringRect);

            if (st.BigBang)
            {
                // ビッグバン後: 外側に細い軌道
                using (var p = new Pen(C(Palette.Dark, st.LightTaskbar ? 200 : 170), W * 0.025f))
                    g.DrawEllipse(p, margin * 0.3f, margin * 0.3f, W - margin * 0.6f, W - margin * 0.6f);
            }

            double prog = star ? 1 : st.Progress;
            if (prog > 0.004)
            {
                using (var p = new Pen(ringCol, rw) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, ringRect, -90, (float)(360 * Math.Min(prog, 0.9999)));
            }

            // ---- 中心の光の玉 (流れ星のときは星形に置き換えるので描かない) ----
            float rc = W * (0.23f + 0.07f * (float)st.Progress);
            var core = new RectangleF(c - rc, c - rc, rc * 2, rc * 2);
            if (!star)
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(core);
                using (var br = new PathGradientBrush(path))
                {
                    br.CenterPoint = new PointF(c - rc * 0.25f, c - rc * 0.3f);
                    br.CenterColor = Mix(tierCol, Color.White, st.Idle ? 0.25 : 0.6);
                    br.SurroundColors = new[] { tierCol };
                    g.FillEllipse(br, core);
                }
            }
            if (st.LightTaskbar && !star)
                using (var p = new Pen(Color.FromArgb(150, 30, 34, 48), W * 0.03f)) g.DrawEllipse(p, core);

            // ---- 超新星を経験したら、玉にきらめきの十字 ----
            if (st.Prestige > 0 && !star)
            {
                float len = rc * (1.15f + 0.12f * Math.Min(st.Prestige, 4));
                float wid = rc * 0.34f;
                int a = st.Idle ? 110 : 230;
                DrawSparkle(g, c, c, len, wid, 0, Color.FromArgb(a, 255, 255, 255));
            }

            // ---- 流れ星: 玉が金色の星に変わってまたたく ----
            if (star)
            {
                double ph = st.StarFrame * 2 * Math.PI / StarFrames;
                float pulse = (float)(0.5 + 0.5 * Math.Sin(ph));
                float len = W * (0.62f + 0.12f * pulse);
                float rot = st.StarFrame * (45f / StarFrames);

                // やわらかい後光
                float hr = W * (0.30f + 0.05f * pulse);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(c - hr, c - hr, hr * 2, hr * 2);
                    using (var br = new PathGradientBrush(path))
                    {
                        br.CenterColor = Color.FromArgb(200, 255, 236, 160);
                        br.SurroundColors = new[] { Color.FromArgb(0, 255, 206, 84) };
                        g.FillEllipse(br, c - hr, c - hr, hr * 2, hr * 2);
                    }
                }
                var edge = st.LightTaskbar ? Color.FromArgb(255, 150, 96, 0) : Color.FromArgb(255, 120, 72, 0);
                DrawSparkle(g, c, c, len + W * 0.05f, len * 0.42f, rot, edge);   // 縁どり
                DrawSparkle(g, c, c, len, len * 0.36f, rot, C(Palette.Gold));
                DrawSparkle(g, c, c, len * 0.55f, len * 0.22f, rot, Color.FromArgb(255, 255, 252, 235));
            }
            else if (!st.Idle)
            {
                // ふつうのときの小さな照り
                float hr = rc * 0.3f;
                using (var br = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                    g.FillEllipse(br, c - rc * 0.42f - hr, c - rc * 0.45f - hr, hr * 2, hr * 2);
            }

            // ---- 買えるものがある印 ----
            if (st.Badge && !star)
            {
                float br = W * 0.15f;
                float bx = W - br - W * 0.02f, by = br + W * 0.02f;
                var edge = st.LightTaskbar ? Color.FromArgb(255, 250, 250, 250) : Color.FromArgb(255, 20, 22, 32);
                using (var b = new SolidBrush(edge)) g.FillEllipse(b, bx - br, by - br, br * 2, br * 2);
                float ir = br * 0.72f;
                using (var b = new SolidBrush(C(Palette.Badge))) g.FillEllipse(b, bx - ir, by - ir, ir * 2, ir * 2);
            }
        }

        /// <summary>4 本の光の筋の星 (✦)。len は端から端まで、waist は中心のくびれの幅。</summary>
        public static void DrawSparkle(Graphics g, float x, float y, float len, float waist, float rotDeg, Color col)
        {
            using (var path = new GraphicsPath())
            {
                float l = len / 2, w = waist / 2 * 0.7071f;
                var pts = new[]
                {
                    new PointF(0, -l), new PointF(w, -w), new PointF(l, 0), new PointF(w, w),
                    new PointF(0, l), new PointF(-w, w), new PointF(-l, 0), new PointF(-w, -w),
                };
                path.AddPolygon(pts);
                using (var m = new Matrix())
                {
                    m.Translate(x, y);
                    m.Rotate(rotDeg);
                    path.Transform(m);
                }
                using (var b = new SolidBrush(col)) g.FillPath(b, path);
            }
        }

        /// <summary>アプリのアイコン (exe やパネルのロゴ) 用の見た目。</summary>
        public static IconState LogoState() => new IconState
        {
            Tier = 8, Progress = 0.72, Prestige = 2, StarFrame = -1,
        };
    }
}
