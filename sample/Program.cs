using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarMini;

namespace TaskbarMiniSample
{
    /// <summary>
    /// 見本: タスクバーの空いているところに時計を出す。
    /// 左クリックで小さな窓をタスクバーの上に出し (Placement.Near)、右クリックで終了。ドラッグで場所を動かせる。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (s, e) =>
            {
                var mini = new MiniScreen(app.Dispatcher, new MiniConfig { Animate = false, Motion = false });
                Window popup = null;

                mini.Clicked += (zone, pt) =>
                {
                    if (popup == null)
                    {
                        popup = new Window
                        {
                            Width = 260, Height = 120, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true,
                            ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(24, 28, 48)),
                            Content = new TextBlock
                            {
                                Text = "Taskbar Mini の見本\n(ほかをクリックすると閉じます)", Foreground = Brushes.White, FontSize = 14,
                                Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap,
                            },
                        };
                        popup.Deactivated += (a, b) => popup.Hide();
                    }
                    popup.Show();
                    Placement.Near(popup, pt);
                    popup.Activate();
                };
                mini.MenuRequested += pt =>
                {
                    var menu = new ContextMenu();
                    var quit = new MenuItem { Header = "終了" };
                    quit.Click += (a, b) => { mini.Dispose(); app.Shutdown(); };
                    menu.Items.Add(quit);
                    menu.IsOpen = true;
                };

                // 1 秒ごとに中身を渡す (変わったときだけ描き直される)
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (a, b) => mini.Update(new MiniContent
                {
                    Light = DateTime.Now.ToString("HH:mm:ss"),
                    Rate = DateTime.Now.ToString("M/d ddd"),
                    Orb = new IconState { Tier = 4, Progress = DateTime.Now.Second / 60.0, StarFrame = -1 },
                });
                timer.Start();
            };
            app.Run();
        }
    }
}
