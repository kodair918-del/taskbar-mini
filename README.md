# Taskbar Mini

Windows 10 / 11 のタスクバーの **中** に、小さな表示を出すしくみ (.NET Framework 4.7.2・C#)。
タスクバーのボタンなどを調べて、**何にもかぶらない空いたすき間** に自動で置きます。ドラッグで好きな場所へ動かせます。

ゲーム [Taskbar Cosmos](https://github.com/kodair918-del/taskbar-cosmos-releases) のミニ画面のしくみを、そのまま取り出したものです。

![sample](docs/sample.png)

## しくみ

- Windows 11 は昔の「ツールバー (デスクバンド)」がなくなったので、タスクバー (エクスプローラーの窓) の **子ウィンドウ** を作って置きます
- よそのプロセスの窓の子になると、入力の順番待ちがエクスプローラーとつながります。こちらが固まるとタスクバーも固まるので、**専用のスレッド** で動かします
- 置き場所は UI Automation でタスクバーのボタン・スタート・通知領域などを調べ、空いているすき間を探します (`TaskbarScan`・`MiniPlacement`)。タスクバーが変わったら置き直し、空きがなければ隠れます
- 描くのは GDI+ で透明度つきの画像を作り、`UpdateLayeredWindow` でそのまま窓にします (1 回 0.3 ミリ秒ほど)
- 複数の画面・縦のタスクバー・拡大率のちがう画面・エクスプローラーの再起動に対応しています

## ファイル (`src/TaskbarMini`)

| ファイル | 中身 |
|---|---|
| `MiniScreen.cs` | 本体。専用のスレッド・置き場所・ドラッグ・クリック |
| `TaskbarScan.cs` | タスクバーの上にあるものを調べる |
| `MiniPlacement.cs` | 空いているすき間を探す |
| `MiniRenderer.cs`・`IconRenderer.cs` | 描く (左に玉と輪、右に 2 行の文字) |
| `Placement.cs` | 小窓をタスクバーのすぐ上に置く (クリックで出すパネル用) |
| `Native.cs`・`NativeMini.cs` | Win32 の呼び出し |
| `Support.cs` | ログ (`Log.LogPath`) と玉の色 (`Palette`) |

## 使い方

```csharp
var mini = new MiniScreen(Application.Current.Dispatcher, new MiniConfig { Animate = false });
mini.Update(new MiniContent { Light = "12:34", Rate = "10/3 金", Orb = new IconState { Tier = 1, Progress = 0.5, StarFrame = -1 } });
mini.Clicked += (zone, pt) => { /* pt はミニ画面の上辺の中央 (画面座標)。Placement.Near(window, pt) でパネルを出せる */ };
mini.MenuRequested += pt => { /* 右クリック */ };
mini.Docked += (monitor, dock, offset) => { /* ドラッグで置いた場所を保存して、次から MiniConfig に入れる */ };
```

見本 (`sample/`) はタスクバーに時計を出します。`dotnet build sample/Sample.csproj -c Release` で作れます。

---

# Taskbar Mini (English)

Show a small widget **inside** the Windows 10/11 taskbar (.NET Framework 4.7.2, C#).
It scans the taskbar with UI Automation and places itself in a **free gap that covers nothing**; users can drag it anywhere.
Extracted from the taskbar mini-screen of the game Taskbar Cosmos.

- A child window of the taskbar (Explorer's window), run on its own thread so Explorer never waits on your UI thread
- Drawn with GDI+ into a per-pixel-alpha bitmap and shown with `UpdateLayeredWindow`
- Handles multiple monitors, vertical taskbars, per-monitor DPI and Explorer restarts
- See `sample/` for a clock in the taskbar. Code comments are in Japanese.

License: MIT
