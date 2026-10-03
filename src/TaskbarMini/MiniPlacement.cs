using System;
using System.Collections.Generic;

namespace TaskbarMini
{
    /// <summary>
    /// タスクバーのミニ画面を「どこに置くか」の計算。画面にもウィンドウにも触らない (テストできるように)。
    /// 座標はすべてタスクバーの左端からの距離 (物理ピクセル)。
    /// </summary>
    public static class MiniPlacement
    {
        public struct Span
        {
            public int L, R;
            public Span(int l, int r) { L = l; R = r; }
            public int Width => R - L;
            public override string ToString() => L + ".." + R;
        }

        /// <summary>ボタンなどが占めている範囲を並べ直し、重なりをまとめる。</summary>
        public static List<Span> Merge(IEnumerable<Span> busy)
        {
            var list = new List<Span>(busy);
            list.Sort((a, b) => a.L.CompareTo(b.L));
            var outp = new List<Span>();
            foreach (var s in list)
            {
                if (s.R <= s.L) continue;
                if (outp.Count > 0 && s.L <= outp[outp.Count - 1].R)
                {
                    var last = outp[outp.Count - 1];
                    if (s.R > last.R) outp[outp.Count - 1] = new Span(last.L, s.R);
                }
                else outp.Add(s);
            }
            return outp;
        }

        /// <summary>
        /// 空いている範囲。edge はタスクバーの両端に残す余白、pad はボタンとの間にあける余白。
        /// </summary>
        public static List<Span> Gaps(IEnumerable<Span> busy, int barWidth, int edge, int pad)
        {
            var gaps = new List<Span>();
            int x = edge;
            foreach (var b in Merge(busy))
            {
                int l = b.L - pad;
                if (l > x) gaps.Add(new Span(x, l));
                x = Math.Max(x, b.R + pad);
            }
            if (barWidth - edge > x) gaps.Add(new Span(x, barWidth - edge));
            return gaps;
        }

        /// <summary>
        /// 幅 w のミニ画面の左端を決める。入る空きがなければ null。
        /// dock 0 (自動): 右端の空き (通知領域のとなり) に右寄せ → だめなら、入る空きのうち一番左に左寄せ。
        /// dock 1: タスクバーの左端から offset の位置、dock 2: 右端から offset の位置 (ミニ画面の右端)。
        ///   その位置がボタンと重なるなら、入る空きのうち一番近い場所へずらす。
        /// </summary>
        public static int? Place(List<Span> gaps, int barWidth, int w, int dock, int offset)
        {
            if (gaps == null || gaps.Count == 0 || w <= 0) return null;

            if (dock == 1 || dock == 2)
            {
                int want = dock == 1 ? offset : barWidth - offset - w;
                int? best = null;
                int bestDist = int.MaxValue;
                foreach (var g in gaps)
                {
                    if (g.Width < w) continue;
                    int x = Math.Max(g.L, Math.Min(want, g.R - w));
                    int d = Math.Abs(x - want);
                    if (d < bestDist) { bestDist = d; best = x; }
                }
                return best;
            }

            var last = gaps[gaps.Count - 1];
            if (last.Width >= w) return last.R - w;
            foreach (var g in gaps)
                if (g.Width >= w) return g.L;
            return null;
        }
    }
}
