using System;
using System.IO;

namespace TaskbarMini
{
    /// <summary>ログ。LogPath を入れたときだけ書く (タスクバーに出せないときの理由など)。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        /// <summary>書き出す先のファイル。null なら書かない。</summary>
        public static string LogPath;

        public static void Write(string msg)
        {
            string path = LogPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("s") + " " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>玉 (左の丸) の色。IconState.Tier の番号で選ぶ。好きな色に差し替えてよい。</summary>
    public static class Palette
    {
        public static (byte R, byte G, byte B)[] Tier =
        {
            (150, 158, 180),    // 0 グレー
            (111, 227, 166),    // 1 緑
            (255, 176, 102),    // 2 オレンジ
            (255, 123, 138),    // 3 赤
            (127, 180, 255),    // 4 青
        };

        public static (byte R, byte G, byte B) TierColor(int tier) => Tier[((tier % Tier.Length) + Tier.Length) % Tier.Length];
        public static (byte R, byte G, byte B) Gold = (255, 206, 84);
        public static (byte R, byte G, byte B) Badge = (255, 92, 122);
        public static (byte R, byte G, byte B) Dark = (140, 110, 255);
    }
}
