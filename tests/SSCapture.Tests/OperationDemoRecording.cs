using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

// docs/usage.mdに載せる「修飾キーの操作」の動画を、実機の入力と画面録画で作る。
// 実際のマウス・キーボードを使うため、利用者の指示があり、PCを操作していないときだけ実行する。
// 背景はこの処理が全画面に出す架空のデスクトップで、利用者の窓や作業中の画面は映さない。
// SSCaptureは録画専用に用意したフォルダの実行ファイルを使い、その隣の設定だけを書き換える。常駐中のSSCaptureと設定には触れない。
// 撮影画面は画面取得から除外されているため、設定の「撮影操作を画面共有・配信に表示する」をONにして共有用の表示を録画する。
// 録画中に利用者の実際のマウス・キー操作を検出したら、押したままのキーを離して中止する。
internal static partial class Program
{
    private static readonly Rectangle DemoMonitor = new(0, 0, 1920, 1080);
    // 動画はHD（1280×720）で、主モニターの左上を等倍で録画する。架空のデスクトップの窓もこの範囲に収める。
    private static readonly Rectangle MainRecordArea = new(0, 0, 1280, 720);
    // スクロール撮影の操作窓は作業領域の右下に出るため、その場面だけは作業領域の右下の1280×720を録画する。
    private static Rectangle ScrollRecordArea => Screen.PrimaryScreen!.WorkingArea is var area
        ? new Rectangle(area.Right - MainRecordArea.Width, area.Bottom - MainRecordArea.Height, MainRecordArea.Width, MainRecordArea.Height) : default;
    private static Rectangle RecordArea = MainRecordArea;
    // 録画範囲のうち、上の字幕（題名と説明、y＜112）と下のキー表示（y≧644）の帯を除いた範囲。
    // SSCaptureにはポインタ横の案内と拡大鏡をこの範囲の内側に置かせ、録画範囲からのはみ出しと字幕との重なりを防ぐ。
    private const int CaptionTop = 112, CaptionBottom = 644;
    private static Rectangle DemoPresentationArea => Rectangle.FromLTRB(RecordArea.Left, RecordArea.Top + CaptionTop, RecordArea.Right, RecordArea.Top + CaptionBottom);
    private const string DemoMarkerFile = "operation-demo.marker";
    private const int DemoSnapPixels = 100;
    private const int DemoMagnifierDots = 20;
    private const int DemoMagnifierZoom = 10;
    private const string DemoScrollPreset = "demo-input-scroll";
    private const string DemoFixedPreset = "demo-input-fixed";
    private static readonly Size DemoFixedSize = new(560, 480);
    // 撮影結果を複数の対象から1枚に合成する場面。結果の題名を「合成された1枚の画像」にする。
    private static readonly HashSet<string> CompositeScenes = ["win-windows-drag", "ctrl-controls-drag", "scroll"];

    private sealed record DemoScene(string Id, string Title, string Detail, Func<DemoDriver, DemoStage, Task> Run, DemoLayout Layout = DemoLayout.Normal);

    // 架空のデスクトップの窓の配置。Dialogは申請フォームに確認ダイアログ（所有ウィンドウ）を重ね、
    // Overlapは資料の窓を申請フォームの右側に重ねて手前に置く。
    // Scrollは資料の窓の代わりに、縦に長い操作手順書の窓を出す。
    private enum DemoLayout { Normal, Dialog, Overlap, Scroll }

    internal static int OperationDemoRecording(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("--operation-demo-recording <作業フォルダ> <録画専用のSSCapture.exe> <GIFの出力先> [場面ID...] を指定してください。");
            return 2;
        }
        string work = Path.GetFullPath(args[1]);
        string app = Path.GetFullPath(args[2]);
        string gifOutput = Path.GetFullPath(args[3]);
        string[] only = args.Skip(4).ToArray();
        DemoScene[] scenes = DemoScenes().Where(scene => only.Length == 0 || only.Contains(scene.Id)).ToArray();
        if (scenes.Length == 0) { Console.Error.WriteLine("指定した場面IDがありません。"); return 2; }
        if (!File.Exists(app)) { Console.Error.WriteLine($"SSCapture.exeが見つかりません: {app}"); return 2; }
        if (Screen.PrimaryScreen?.Bounds != DemoMonitor)
        {
            Console.Error.WriteLine($"主モニターが{DemoMonitor.Width}×{DemoMonitor.Height}（左上0,0）のときだけ実行できます。現在: {Screen.PrimaryScreen?.Bounds}");
            return 2;
        }
        string? ffmpeg = FindOnPath("ffmpeg.exe");
        if (ffmpeg is null) { Console.Error.WriteLine("ffmpeg.exeがPATHにありません。"); return 2; }
        string appPath = Path.GetFullPath(app);
        if (Process.GetProcessesByName("SSCapture").Any(process => SameExecutable(process, appPath)))
        {
            Console.Error.WriteLine("指定したSSCapture.exeが起動中です。常駐中のものではなく、録画専用に用意したフォルダを指定してください。");
            return 2;
        }
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(gifOutput);
        try { PrepareDemoSettings(Path.GetDirectoryName(appPath)!, Path.Combine(work, "captures")); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        File.Copy(@"C:\Windows\Fonts\YuGothB.ttc", Path.Combine(work, "font.ttc"), overwrite: true);

        Application.SetDefaultFont(new Font("Yu Gothic UI", 12F));
        using var stage = new DemoStage();
        int exitCode = 1;
        stage.Backdrop.Shown += async (_, _) =>
        {
            using var driver = new DemoDriver(appPath, ffmpeg, work);
            try
            {
                await Task.Delay(800);
                foreach (DemoScene scene in scenes)
                {
                    await driver.RecordAsync(scene, stage);
                    await driver.RenderAsync(scene, gifOutput);
                }
                exitCode = 0;
            }
            catch (DemoAbortException ex) { Console.Error.WriteLine("中止しました: " + ex.Message); exitCode = 3; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { stage.Backdrop.Close(); }
        };
        Application.Run(stage.Backdrop);
        return exitCode;
    }

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), file)).FirstOrDefault(File.Exists);

    private static bool SameExecutable(Process process, string path)
    {
        try { return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase); }
        catch (System.ComponentModel.Win32Exception) { return false; }
        finally { process.Dispose(); }
    }

    // 録画専用のフォルダの設定を作る。目印のない既存の設定は利用者のものとみなし、上書きしない。
    private static void PrepareDemoSettings(string appDirectory, string captures)
    {
        string data = Path.Combine(appDirectory, "data");
        string settingsPath = Path.Combine(data, "settings.json");
        string marker = Path.Combine(data, DemoMarkerFile);
        if (File.Exists(settingsPath) && !File.Exists(marker))
            throw new InvalidOperationException($"{settingsPath} は録画用に作った設定ではないため、上書きしません。録画専用に用意したフォルダのSSCapture.exeを指定してください。");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(captures);
        File.WriteAllText(marker, "--operation-demo-recording が作った録画用の設定です。\n");
        AppSettings settings = new AppSettings().ApplyDefaults();
        settings = (settings with
        {
            // 録画中の撮影でクリップボードを上書きしないよう、作業フォルダへのファイル保存にする。
            CapturePresets = settings.CapturePresets
                .Select(preset => preset with { IsDefault = preset.BuiltInKey == "initial-output-file", SaveDirectory = captures }).ToArray(),
            ShareCapturePresentation = true,
            PlayCaptureSound = false,
            KeepHistory = false,
            // 既定の20pxではグリッドの変化が動画で見分けにくいため、100pxにする。動画の説明にもこの値を書く。
            // ウィンドウスナップ距離は既定の20pxのままにする。100pxにすると、窓の辺より先にモニターの端へ吸着してしまう。
            GridStepPixels = DemoSnapPixels,
            // 動画用の拡大鏡は20×20画素を10倍で表示する（レンズは200px。表示の切り替えは中クリック）。
            MagnifierDotCount = DemoMagnifierDots,
            MagnifierZoom = DemoMagnifierZoom,
            // 画面右下の操作ガイドは録画範囲の外にあるが、右下を録画するスクロール撮影では映り込むため表示しない。
            ShowSelectionGuide = false,
            // Spaceで切り替える基点を「四方と中心」にする（初期値は四方）。固定サイズの枠の基点を中心にできる。
            // ドラッグ中も左下の次が中心になるため、ドラッグ中のSpaceの場面は左下の角から押さない。
            CaptureKeys = settings.CaptureKeys with { SpaceCycle = CaptureAnchorCycle.CornersAndCenter },
            // スクロール撮影と固定サイズの場面で --input-preset に指定する入力プリセット。
            // 固定サイズは、資料の窓（540×440）を中央に置いた枠が録画範囲に収まる大きさにする。
            SavedRegions = [.. settings.SavedRegions,
                SavedRegion.FromSettings("スクロール撮影（録画用）", new AppSettings() with { DefaultMode = CaptureMode.Scroll })
                    with { Id = DemoScrollPreset, UseFixedCoordinates = false, Mode = CaptureMode.Scroll, IsDefault = false },
                SavedRegion.FromSettings($"固定サイズ {DemoFixedSize.Width}×{DemoFixedSize.Height}",
                    new AppSettings() with { DefaultMode = CaptureMode.FixedRegion, FixedWidth = DemoFixedSize.Width, FixedHeight = DemoFixedSize.Height })
                    with { Id = DemoFixedPreset, UseFixedCoordinates = false, Mode = CaptureMode.FixedRegion, IsDefault = false }]
        }).ApplyDefaults();
        new SettingsStore(settingsPath).Save(settings);
    }

    // 掲載ページ（docs/operation-videos.md）と同じ順に並べる。
    private static DemoScene[] DemoScenes() =>
    [
        // 基本の撮影：範囲・ウィンドウ・モニター
        new("plain-drag", "左ドラッグで囲う（Snipping Tool と同じ操作）", "ボタンを押した位置から離した位置までの範囲を撮影", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            await d.StartAsync(chart.Location + new Size(-70, -50));
            await d.WaitAsync(600);
            await d.MoveAsync(chart.Location + new Size(-12, -14), 600);
            await d.WaitAsync(400);
            d.LeftDown();
            await d.MoveAsync(new Point(chart.Right + 12, chart.Bottom + 34), 1300);
            await d.WaitAsync(800);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-cancel", "ドラッグ中に右クリック／Esc", "押している途中の範囲だけを取り消し、選び直す", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            await d.StartAsync(chart.Location + new Size(160, 90));
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(chart.Location + new Size(380, 250), 800);
            await d.WaitAsync(500);
            await d.RightClickAsync();
            d.LeftUp();
            await d.WaitAsync(600);
            await d.MoveAsync(chart.Location + new Size(50, 30), 700);
            d.LeftDown();
            await d.MoveAsync(chart.Location + new Size(230, 270), 800);
            await d.WaitAsync(500);
            await d.TapKeyAsync(0x1B, "Esc", 1, 900);
            d.LeftUp();
            await d.WaitAsync(600);
            await d.MoveAsync(chart.Location, 700);
            d.LeftDown();
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height), 1000);
            await d.WaitAsync(700);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("plain-window", "撮影前にクリック", "ポインタの下のウィンドウを、見えているままに撮影（手前に重なった窓も写る）", async (d, s) =>
        {
            Rectangle entry = s.Entry.Bounds;
            await d.StartAsync(new Point(640, 670));
            await d.WaitAsync(600);
            await d.MoveAsync(new Point(entry.Left + 220, entry.Top + 260), 1000);
            await d.WaitAsync(1000);
            // 押している間は対象のウィンドウが強調され、離すとその枠内に見えている画面を切り抜く。
            d.LeftDown();
            await d.WaitAsync(1200);
            d.LeftUp();
            await d.FinishAsync();
        }, DemoLayout.Overlap),
        new("win-back-window", "撮影前に Win を押しながらクリック（重なった窓）", "後ろにあるウィンドウを前に出して、隠れた部分も含めて撮影", async (d, s) =>
        {
            Rectangle entry = s.Entry.Bounds, report = s.Report.Bounds;
            await d.StartAsync(new Point(640, 670));
            await d.WaitAsync(400);
            // 背景の上でWinを押すと背景全体が対象になるため、先に資料の窓の上へ移る。
            await d.MoveAsync(s.Center(report), 900);
            await d.WaitAsync(300);
            d.Press(DemoKey.Win);
            await d.WaitAsync(1300);
            // 申請フォームの見えている左側を指すと、隠れていた右側も含めて手前に表示される。
            await d.MoveAsync(new Point(entry.Left + 220, entry.Top + 260), 1000);
            await d.WaitAsync(1600);
            await d.ClickAsync();
            await d.FinishAsync();
        }, DemoLayout.Overlap),
        new("win-window", "撮影前に Win を押しながらクリック", "ポインタの下のウィンドウを、手前のダイアログごと撮影", async (d, s) =>
        {
            Rectangle entry = s.Entry.Bounds;
            await d.StartAsync(new Point(900, 660));
            await d.WaitAsync(400);
            // 背景の上でWinを押すと背景全体が対象になるため、先に申請フォームの上へ移る。
            await d.MoveAsync(new Point(entry.Left + 110, entry.Bottom - 110), 800);
            await d.WaitAsync(300);
            d.Press(DemoKey.Win);
            await d.WaitAsync(1300);
            await d.MoveAsync(s.Center(s.Dialog.Bounds), 800);
            await d.WaitAsync(1200);
            await d.MoveAsync(new Point(entry.Left + 130, entry.Bottom - 90), 800);
            await d.WaitAsync(1000);
            await d.ClickAsync();
            await d.FinishAsync();
        }, DemoLayout.Dialog),
        new("win-windows-drag", "撮影前に Win を押しながら左ドラッグ", "通ったウィンドウを位置と重なり順のまま1枚にまとめる", async (d, s) =>
        {
            Rectangle entry = s.Entry.Bounds;
            await d.StartAsync(new Point(900, 660));
            await d.WaitAsync(400);
            // 背景の上でWinを押すと背景全体が対象になるため、先に申請フォームの上へ移る。
            // 資料の窓は申請フォームの右端に重ねてあり、ドラッグは背景を通らない。
            await d.MoveAsync(new Point(entry.Left + 110, entry.Bottom - 110), 800);
            await d.WaitAsync(300);
            d.Press(DemoKey.Win);
            await d.WaitAsync(1000);
            d.LeftDown();
            await d.MoveAsync(s.Center(s.Dialog.Bounds), 800);
            await d.MoveAsync(s.Center(s.Report) + new Size(-100, 40), 900);
            await d.MoveAsync(s.Center(s.Report) + new Size(80, 0), 500);
            await d.WaitAsync(900);
            d.LeftUp();
            await d.FinishAsync();
        }, DemoLayout.Dialog),
        new("shift-monitor", "撮影前に Shift＋左クリック", "ポインタのあるモニター全体を撮影", async (d, s) =>
        {
            await d.StartAsync(new Point(900, 660));
            await d.WaitAsync(700);
            d.Press(DemoKey.Shift);
            await d.WaitAsync(1200);
            await d.ClickAsync();
            await d.FinishAsync();
        }),

        // コントロールの撮影：Ctrl
        new("ctrl-control", "撮影前に Ctrl を押しながらクリック", "ポインタの下のボタンや入力欄を1つだけ撮影", async (d, s) =>
        {
            await d.StartAsync(s.Center(s.NameBox) + new Size(-120, -60));
            await d.WaitAsync(400);
            // コントロールのない位置でCtrlを押すと「対象のコントロールが見つかりません」と出るため、先に入力欄の上へ移る。
            await d.MoveAsync(s.Center(s.NameBox), 500);
            await d.WaitAsync(300);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(1000);
            await d.MoveAsync(s.Center(s.Notify), 600);
            await d.WaitAsync(800);
            await d.MoveAsync(s.Center(s.Send), 600);
            await d.WaitAsync(1000);
            await d.ClickAsync();
            await d.FinishAsync();
        }),
        new("ctrl-controls-drag", "撮影前に Ctrl を押しながら左ドラッグ", "通ったコントロールを元の位置関係のまま1枚にまとめる", async (d, s) =>
        {
            await d.StartAsync(s.Center(s.NameBox) + new Size(-120, -60));
            await d.WaitAsync(400);
            // コントロールのない位置でCtrlを押すと「対象のコントロールが見つかりません」と出るため、先に入力欄の上へ移る。
            await d.MoveAsync(s.Center(s.NameBox), 500);
            await d.WaitAsync(300);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(900);
            d.LeftDown();
            await d.MoveAsync(s.Center(s.Department), 600);
            await d.MoveAsync(s.Center(s.Notify), 700);
            await d.MoveAsync(s.Center(s.Send), 700);
            await d.WaitAsync(800);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("ctrl-wheel-margin", "撮影前に Ctrl を押しながらホイール回転", "撮影対象の余白を広げる（1目盛りで四辺1pxずつ）", async (d, s) =>
        {
            await d.StartAsync(s.Center(s.Send) + new Size(-140, -70));
            await d.WaitAsync(400);
            // コントロールのない位置でCtrlを押すと「対象のコントロールが見つかりません」と出るため、先に送信ボタンの上へ移る。
            await d.MoveAsync(s.Center(s.Send), 600);
            await d.WaitAsync(300);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(1000);
            await d.WheelAsync(24, 70);
            await d.WaitAsync(1200);
            await d.ClickAsync();
            await d.FinishAsync();
        }),

        // ドラッグ中のキー
        new("drag-space-corners", "ドラッグ中に Space", "範囲を保ったまま、ポインタを次の角へ移して反対側から調整する", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            // 左下から右上へドラッグし、Spaceで右上→右下→左下と移る（録画では左下の次が中心になるため、左下からは押さない）。
            Point start = chart.Location + new Size(40, 200);
            await d.StartAsync(start);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(start + new Size(280, -170), 1000);
            await d.WaitAsync(700);
            // Spaceを押すたびに、ポインタが範囲の角を時計回りに移る。移った角から範囲を広げ直せる。
            await d.TapKeyAsync(0x20, "Space", 1, 900, movesPointer: true);
            await d.MoveAsync(d.Pointer + new Size(30, 30), 800);
            await d.WaitAsync(600);
            await d.TapKeyAsync(0x20, "Space", 1, 900, movesPointer: true);
            await d.MoveAsync(d.Pointer + new Size(-25, 20), 800);
            await d.WaitAsync(900);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-win-aspect", "ドラッグ中に Win", "押したときの縦横比を保ったまま広げる", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            await d.StartAsync(chart.Location);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(chart.Location + new Size(chart.Width / 2, chart.Height / 2), 700);
            await d.WaitAsync(400);
            d.Press(DemoKey.Win);
            await d.WaitAsync(500);
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height / 2), 700);
            await d.MoveAsync(chart.Location + new Size(chart.Width * 2 / 3, chart.Height), 700);
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height), 700);
            await d.WaitAsync(700);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-ctrl-center", "ドラッグ中に Ctrl", "ドラッグの起点を中心に、四方へ広げる", async (d, s) =>
        {
            Point center = s.Center(s.Chart);
            await d.StartAsync(center);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(center + new Size(60, 40), 400);
            await d.WaitAsync(300);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(500);
            await d.MoveAsync(center + new Size(230, 110), 900);
            await d.MoveAsync(center + new Size(160, 160), 600);
            await d.WaitAsync(700);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-ctrl-shift", "ドラッグ中に Ctrl＋Shift", "起点を中心に、縦横比を保って広げる", async (d, s) =>
        {
            Point center = s.Center(s.Chart);
            await d.StartAsync(center);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(center + new Size(80, 45), 400);
            await d.WaitAsync(300);
            d.Press(DemoKey.Ctrl, DemoKey.Shift);
            await d.WaitAsync(500);
            await d.MoveAsync(center + new Size(230, 80), 800);
            await d.MoveAsync(center + new Size(140, 160), 700);
            await d.MoveAsync(center + new Size(230, 150), 600);
            await d.WaitAsync(700);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-alt-shift", "ドラッグ中に Alt＋Shift", "高さを固定して、横だけ変える", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            await d.StartAsync(chart.Location);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(chart.Location + new Size(chart.Width * 2 / 3, chart.Height), 800);
            await d.WaitAsync(400);
            d.Press(DemoKey.Shift, DemoKey.Alt);
            await d.WaitAsync(500);
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height / 2), 700);
            await d.MoveAsync(chart.Location + new Size(chart.Width / 3, chart.Height + 50), 900);
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height + 25), 800);
            await d.WaitAsync(700);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-alt-ctrl", "ドラッグ中に Alt＋Ctrl", "幅を固定して、縦だけ変える", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            await d.StartAsync(chart.Location);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height / 2), 800);
            await d.WaitAsync(400);
            d.Press(DemoKey.Ctrl, DemoKey.Alt);
            await d.WaitAsync(500);
            await d.MoveAsync(chart.Location + new Size(chart.Width / 2, chart.Height), 700);
            await d.MoveAsync(chart.Location + new Size(chart.Width + 30, chart.Height / 3), 900);
            await d.MoveAsync(chart.Location + new Size(chart.Width / 2, chart.Height + 20), 800);
            await d.WaitAsync(700);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-win-alt-grid", "ドラッグ中に Win＋Alt", $"幅と高さをグリッド間隔（この動画では{DemoSnapPixels}px）にそろえる", async (d, s) =>
        {
            Rectangle chart = s.Chart;
            Point start = chart.Location + new Size(7, 5);
            await d.StartAsync(start);
            await d.WaitAsync(600);
            d.LeftDown();
            await d.MoveAsync(start + new Size(143, 87), 700);
            await d.WaitAsync(500);
            d.Press(DemoKey.Alt, DemoKey.Win);
            await d.WaitAsync(700);
            // グリッドの段差が見えるよう、ゆっくり大きく動かす。
            await d.MoveAsync(start + new Size(chart.Width - 20, chart.Height - 30), 2600);
            await d.WaitAsync(500);
            await d.MoveAsync(start + new Size(150, 110), 2200);
            await d.WaitAsync(500);
            await d.MoveAsync(start + new Size(330, 230), 1500);
            await d.WaitAsync(800);
            d.LeftUp();
            await d.FinishAsync();
        }),

        // ウィンドウへの吸着：Alt
        new("drag-alt-snap", "Alt を押しながらドラッグ", "ウィンドウの角に吸着させて、ウィンドウをぴったり囲う", async (d, s) =>
        {
            await SnapToWindowAsync(d, s.Report.Bounds);
            await d.WaitAsync(800);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("snap-lock-height", "ウィンドウに吸着してから Alt＋Shift", "ウィンドウの高さのまま幅だけ縮め、左側の部分をきれいに撮影", async (d, s) =>
        {
            Rectangle report = s.Report.Bounds;
            await SnapToWindowAsync(d, report);
            d.Press(DemoKey.Shift);
            await d.WaitAsync(700);
            // 高さは固定されるため、ポインタを上下に動かしても変わらない。
            await d.MoveAsync(new Point(report.Left + 300, report.Bottom - 100), 1400);
            await d.WaitAsync(500);
            await d.MoveAsync(new Point(report.Left + 280, report.Bottom + 40), 800);
            await d.WaitAsync(1000);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("snap-lock-width", "ウィンドウに吸着してから Alt＋Ctrl", "ウィンドウの幅のまま高さだけ縮め、上側の部分をきれいに撮影", async (d, s) =>
        {
            Rectangle report = s.Report.Bounds;
            await SnapToWindowAsync(d, report);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(700);
            // 幅は固定されるため、ポインタを左右に動かしても変わらない。
            await d.MoveAsync(new Point(report.Right - 130, report.Top + 240), 1400);
            await d.WaitAsync(500);
            await d.MoveAsync(new Point(report.Right + 25, report.Top + 260), 800);
            await d.WaitAsync(1000);
            d.LeftUp();
            await d.FinishAsync();
        }),

        // 拡大鏡・固定サイズ・スクロール撮影
        new("magnifier", "拡大鏡と十字キーで画素単位に合わせる", $"中クリックで拡大鏡を表示し、十字キーで1pxずつ微調整（この動画では拡大倍率{DemoMagnifierZoom}倍）", async (d, s) =>
        {
            // グラフやボタンの角は境界がぼやけるため、1pxの枠線がはっきりした複数行の入力欄の角に合わせる。
            Rectangle notes = s.Notes.Parent!.RectangleToScreen(s.Notes.Bounds);
            // 拡大鏡は10×10画素を映すため、最初から入力欄の角が拡大鏡に入る位置（角から4px以内）で始める。
            await d.StartAsync(notes.Location + new Size(-4, -3));
            await d.WaitAsync(700);
            await d.MiddleClickAsync();
            await d.WaitAsync(1000);
            await d.TapKeyAsync(0x27, "→", 4, 450, extended: true, movesPointer: true);
            await d.TapKeyAsync(0x28, "↓", 3, 450, extended: true, movesPointer: true);
            await d.WaitAsync(900);
            d.LeftDown();
            Point end = new(notes.Right - 1, notes.Bottom - 1);
            await d.MoveAsync(end + new Size(4, 3), 1300);
            await d.WaitAsync(700);
            await d.TapKeyAsync(0x25, "←", 4, 450, extended: true, movesPointer: true);
            await d.TapKeyAsync(0x26, "↑", 3, 450, extended: true, movesPointer: true);
            await d.WaitAsync(1000);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("fixed-size", "固定サイズの枠を置いて撮影", $"入力プリセット「固定サイズ {DemoFixedSize.Width}×{DemoFixedSize.Height}」の枠を、Spaceで基点を中心にして窓の中央に合わせる", async (d, s) =>
        {
            Rectangle report = s.Report.Bounds;
            await d.StartAsync(new Point(160, 150), "--capture-preset", "--input-preset", DemoFixedPreset);
            await d.WaitAsync(800);
            // Shift＋矢印（10px移動）は、Shiftを押している間はモニター全体の選択表示になって枠が見えなくなるため、この動画では使わない。
            // 枠は基点（最初は左上）でポインタに付いて動く。録画範囲と字幕の帯の内側に枠が収まる位置で動かす。
            await d.MoveAsync(new Point(300, 130), 1000);
            await d.WaitAsync(700);
            // 既定ではSpaceで左上→右上→右下→左下→中心と移る。枠はそのままで、ポインタだけが次の基点へ移る。4回で中心になる。
            await d.TapKeyAsync(0x20, "Space", 4, 1000, movesPointer: true);
            await d.WaitAsync(500);
            // 中心を基点にしたまま資料の窓の中心へ動かし、窓が枠の中央に収まるようにする。
            await d.MoveAsync(s.Center(report), 1200);
            await d.WaitAsync(800);
            // ホイールで枠の四辺を少し広げ、窓の周りに余白を取る。
            await d.WheelAsync(10, 90);
            await d.WaitAsync(1000);
            await d.ClickAsync();
            await d.FinishAsync();
        }),
        new("scroll", "スクロール撮影", "囲んだ範囲を下へスクロールし、前後を照合して縦長の1枚にする（入力プリセットで「スクロール撮影」を選ぶ）", async (d, s) =>
        {
            Rectangle view = s.ManualView.RectangleToScreen(s.ManualView.ClientRectangle);
            Point topLeft = view.Location + new Size(12, 12);
            Point bottomRight = new(view.Right - 12, view.Top + 400);
            await d.StartAsync(topLeft + new Size(-60, -40), "--capture-select", "--input-preset", DemoScrollPreset);
            await d.WaitAsync(700);
            await d.MoveAsync(topLeft, 600);
            await d.WaitAsync(400);
            d.LeftDown();
            await d.MoveAsync(bottomRight, 1000);
            await d.WaitAsync(500);
            d.LeftUp();
            Rectangle confirm = await d.FindScrollConfirmAsync();
            await d.WaitAsync(800);
            await d.MoveAsync(new Point((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2), 600);
            await d.WaitAsync(500);
            await d.WheelAsync(18, 450, down: true);
            await d.WaitAsync(1200);
            await d.MoveAsync(s.Center(confirm), 1000);
            await d.WaitAsync(500);
            await d.ClickAsync();
            await d.FinishAsync();
        }, DemoLayout.Scroll)
    ];

    // Altを押しながら窓の左上の角のすぐ外で押し、右下の角のすぐ外までドラッグして、窓をぴったり囲う。
    // 角から離れた位置で始めると、近くにある別の窓の辺やモニターの端に吸着してしまうため、
    // 窓の見えている枠から既定のスナップ距離（20px）以内で押して離す。窓の左右と下には約7pxの見えない枠がある。
    // 左ボタンとAltを押したまま戻る。
    private static async Task SnapToWindowAsync(DemoDriver d, Rectangle window)
    {
        await d.StartAsync(window.Location + new Size(-90, -70));
        await d.WaitAsync(600);
        d.Press(DemoKey.Alt);
        await d.WaitAsync(500);
        await d.MoveAsync(window.Location + new Size(-6, -6), 1100);
        await d.WaitAsync(900);
        d.LeftDown();
        await d.MoveAsync(new Point(window.Right - 260, window.Bottom - 200), 900);
        await d.WaitAsync(300);
        await d.MoveAsync(new Point(window.Right + 4, window.Bottom + 4), 1300);
        await d.WaitAsync(1000);
    }

    private enum DemoKey : byte { Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B }

    private sealed class DemoAbortException(string message) : Exception(message);

    // 全画面の架空のデスクトップ。録画中は前面に置き、利用者の窓を隠す。
    // 背景もSSCaptureからは通常のウィンドウに見え、申請フォーム・資料の窓を所有している。
    // Winを押している間にポインタが背景の上にあると、背景と全部の窓がまとめて対象になるため、
    // Winの場面では窓の上へ移ってからWinを押し、窓の間の隙間も通らない。
    // 背景を入力透過にして候補から外すと、裏にある利用者の実際の窓が対象になり録画に映るため、そうしない。
    private sealed class DemoStage : IDisposable
    {
        internal readonly Form Backdrop, Entry, Report, Dialog, Manual;
        internal readonly Panel ManualView;
        // 左上の字幕（y≒110まで）と左下のキー表示（y≒644から）をなるべく避けて、1280×720に収める。
        private static readonly Rectangle EntryBounds = new(40, 128, 620, 500);
        private static readonly Rectangle ReportBounds = new(700, 150, 540, 440);
        private static readonly Rectangle OverlapReportBounds = new(420, 200, 540, 440);
        // Winで左ドラッグする場面は、申請フォームから資料の窓へ背景を通らずに移れるよう、端を重ねる。
        private static readonly Rectangle DialogReportBounds = new(620, 150, 540, 440);
        internal readonly TextBox NameBox, Notes;
        internal readonly ComboBox Department;
        internal readonly CheckBox Notify, Attach;
        internal readonly Button Send, Cancel;
        private readonly Panel chartPanel;

        internal DemoStage()
        {
            Backdrop = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = DemoMonitor,
                // 最前面にすると、コントロール選択でSSCaptureが元の窓を前面へ戻したときに撮影画面より上に出てしまう。
                // 通常の窓として前面に置き、タスクバーは録画範囲（作業領域）から外す。
                ShowInTaskbar = false, BackColor = Color.FromArgb(180, 204, 230), Text = "操作動画の背景"
            };
            Backdrop.Paint += (_, e) =>
            {
                using var wallpaper = new LinearGradientBrush(Backdrop.ClientRectangle,
                    Color.FromArgb(214, 228, 240), Color.FromArgb(160, 190, 222), LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillRectangle(wallpaper, Backdrop.ClientRectangle);
            };

            Entry = new Form
            {
                Text = "申請フォーム（サンプル） - 業務アプリ", StartPosition = FormStartPosition.Manual, Bounds = EntryBounds,
                FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Color.White
            };
            Label Caption(string text, int y) => new() { Text = text, AutoSize = true, Location = new Point(36, y + 4) };
            NameBox = new TextBox { Location = new Point(150, 30), Width = 300, Text = "山田 花子" };
            Department = new ComboBox { Location = new Point(150, 80), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            Department.Items.AddRange(["営業部", "開発部", "総務部"]);
            Department.SelectedIndex = 1;
            Notes = new TextBox { Location = new Point(150, 130), Size = new Size(420, 130), Multiline = true, Text = "新機能の公開に合わせて、問い合わせ窓口の体制を見直します。" };
            Notify = new CheckBox { Text = "上長に通知する", AutoSize = true, Location = new Point(150, 276), Checked = true };
            Attach = new CheckBox { Text = "添付ファイルあり", AutoSize = true, Location = new Point(150, 314) };
            Send = new Button { Text = "送信", Location = new Point(318, 380), Size = new Size(120, 44) };
            Cancel = new Button { Text = "キャンセル", Location = new Point(450, 380), Size = new Size(120, 44) };
            Entry.Controls.AddRange([Caption("氏名", 30), NameBox, Caption("部署", 80), Department, Caption("内容", 130), Notes, Notify, Attach, Send, Cancel]);

            Report = new Form
            {
                Text = "週次レポート（サンプル） - 資料ビューアー", StartPosition = FormStartPosition.Manual, Bounds = ReportBounds,
                FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Color.White
            };
            chartPanel = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.White };
            chartPanel.Paint += (_, e) => PaintReport(e.Graphics, chartPanel.ClientSize);
            Report.Controls.Add(chartPanel);

            // 申請フォームが所有する確認ダイアログ。ウィンドウ撮影で、所有ウィンドウごと撮れることを見せる。
            Dialog = new Form
            {
                Text = "送信の確認", StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(EntryBounds.Left + 150, EntryBounds.Top + 150, 380, 190),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Color.White
            };
            Dialog.Controls.AddRange([
                new Label { Text = "この内容で申請を送信しますか？", AutoSize = true, Location = new Point(30, 34) },
                new Button { Text = "はい", Location = new Point(130, 92), Size = new Size(110, 40) },
                new Button { Text = "いいえ", Location = new Point(250, 92), Size = new Size(110, 40) }]);

            // スクロール撮影の場面で使う、縦に長い操作手順書。前後の画面を照合できるよう、節ごとに内容と色を変える。
            Manual = new Form
            {
                Text = "操作手順書（サンプル） - ドキュメントビューアー", StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(ScrollRecordArea.Left + 40, ScrollRecordArea.Top + 130, 540, 520),
                FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Color.White
            };
            ManualView = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            var manualPage = new DoubleBufferedPanel { Location = Point.Empty, Size = new Size(500, 2600), BackColor = Color.White };
            manualPage.Paint += (_, e) => PaintManual(e.Graphics);
            ManualView.Controls.Add(manualPage);
            Manual.Controls.Add(ManualView);

            Backdrop.Load += (_, _) =>
            {
                Entry.Show(Backdrop);
                Report.Show(Backdrop);
            };
        }

        private static void PaintManual(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var titleFont = new Font("Yu Gothic UI", 18F, FontStyle.Bold);
            using var headingFont = new Font("Yu Gothic UI", 13F, FontStyle.Bold);
            using var bodyFont = new Font("Yu Gothic UI", 11F);
            graphics.DrawString("問い合わせ窓口の運用手順", titleFont, Brushes.Black, 30, 24);
            string[] titles = ["受付", "内容の確認", "担当者の割り当て", "一次回答", "調査の依頼", "進捗の共有", "回答の作成", "上長の確認", "回答の送信", "記録の整理", "週次の集計", "改善の検討"];
            Color[] colors = [Color.FromArgb(76, 140, 200), Color.FromArgb(230, 126, 60), Color.FromArgb(90, 170, 110), Color.FromArgb(170, 110, 190),
                Color.FromArgb(220, 180, 60), Color.FromArgb(80, 170, 180)];
            int y = 90;
            for (int i = 0; i < titles.Length; i++)
            {
                graphics.DrawString($"手順{i + 1}　{titles[i]}", headingFont, Brushes.Black, 30, y);
                graphics.DrawString($"{titles[i]}では、記録票の項目{i + 2}つを確認してから次へ進みます。", bodyFont, Brushes.DimGray, 30, y + 34);
                using var brush = new SolidBrush(colors[i % colors.Length]);
                int width = 140 + (i * 53) % 300;
                graphics.FillRectangle(brush, 30, y + 66, width, 34 + (i % 3) * 12);
                graphics.DrawString($"図{i + 1}", bodyFont, Brushes.White, 38, y + 72);
                y += 200;
            }
        }

        // 操作手順書の窓を出し入れする。出すときは先頭までスクロールを戻す。
        private void ArrangeManual(bool visible)
        {
            if (visible)
            {
                ManualView.AutoScrollPosition = Point.Empty;
                if (!Manual.Visible) Manual.Show(Backdrop);
                Manual.BringToFront();
            }
            else Manual.Hide();
        }

        // 場面ごとの窓の配置にする。背景を前面にした後に呼ぶ。
        internal void Arrange(DemoLayout layout)
        {
            Report.Bounds = layout switch
            {
                DemoLayout.Overlap => OverlapReportBounds,
                DemoLayout.Dialog => DialogReportBounds,
                _ => ReportBounds,
            };
            if (layout == DemoLayout.Dialog)
            {
                if (!Dialog.Visible) Dialog.Show(Entry);
            }
            else Dialog.Hide();
            // スクロール撮影は録画範囲が右下にずれるため、端だけ映り込む申請フォームも隠す。
            Entry.Visible = layout != DemoLayout.Scroll;
            Report.Visible = layout != DemoLayout.Scroll;
            ArrangeManual(layout == DemoLayout.Scroll);
            if (layout == DemoLayout.Overlap) Report.BringToFront();
            Backdrop.Update();
        }

        // 資料の窓のグラフの範囲（画面座標）。
        internal Rectangle Chart => chartPanel.RectangleToScreen(ChartArea(chartPanel.ClientSize));

        internal Point Center(Control control) => Center(Bounds(control));
        internal Rectangle Bounds(Control control) => control.RectangleToScreen(control.ClientRectangle);
        internal Point Center(Rectangle rectangle) => new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);

        private static readonly int[] BarValues = [120, 180, 150, 230, 260, 210];

        private static Rectangle ChartArea(Size client) => new(50, 80, client.Width - 100, client.Height - 170);

        private static void PaintReport(Graphics graphics, Size client)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var headingFont = new Font("Yu Gothic UI", 18F, FontStyle.Bold);
            using var bodyFont = new Font("Yu Gothic UI", 11F);
            graphics.DrawString("問い合わせ件数の推移", headingFont, Brushes.Black, 36, 24);
            Rectangle chart = ChartArea(client);
            using (var axis = new Pen(Color.FromArgb(150, 150, 150), 2))
            {
                graphics.DrawLine(axis, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
                graphics.DrawLine(axis, chart.Left, chart.Top, chart.Left, chart.Bottom);
            }
            int[] values = BarValues;
            string[] labels = ["月", "火", "水", "木", "金", "土"];
            int slot = chart.Width / values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                using var bar = new SolidBrush(i is 3 or 4 ? Color.FromArgb(230, 126, 60) : Color.FromArgb(76, 140, 200));
                int height = values[i] * chart.Height / 280;
                graphics.FillRectangle(bar, chart.Left + i * slot + slot / 4, chart.Bottom - height, slot / 2, height);
                graphics.DrawString(labels[i], bodyFont, Brushes.DimGray, chart.Left + i * slot + slot / 2 - 9, chart.Bottom + 8);
            }
            graphics.DrawString("木・金は新機能の公開に伴い増加。", bodyFont, Brushes.DimGray, 36, client.Height - 56);
        }

        public void Dispose()
        {
            Manual.Dispose();
            Dialog.Dispose();
            Report.Dispose();
            Entry.Dispose();
            Backdrop.Dispose();
        }

        private sealed class DoubleBufferedPanel : Panel
        {
            internal DoubleBufferedPanel() => DoubleBuffered = true;
        }
    }

    // 実際の入力を送り、画面を録画する。押しているキーとボタンの変化を記録し、後で字幕にする。
    private sealed class DemoDriver : IDisposable
    {
        private readonly string app, ffmpeg, work;
        private readonly HashSet<DemoKey> keys = [];
        private readonly List<(double At, string Label)> labels = [];
        private readonly Stopwatch clock = new();
        private readonly Point originalCursor;
        private readonly HookProc mouseHook, keyboardHook;
        private readonly nint mouseHandle, keyboardHandle;
        private bool leftDown, disposed;
        private string? physicalInput;
        private Process? capture;
        private Task<string>? captureOutput, captureError;
        private Process? recorder;
        private double sceneStart, sceneEnd;
        private DateTime sceneWallStart;
        private Rectangle? hiddenPanel;
        private double hiddenPanelAt;
        private Point cursor;
        // 送った、またはSSCaptureが動かした後に読み直したポインタの位置。
        internal Point Pointer => cursor;
        private long pointerMovedByApplicationUntil;
        // 移動の通知は送った直後とは限らず、少し前に送った位置で届くことがあるため、直近の位置を覚えておく。
        private readonly Queue<Point> sentPositions = new();

        private void Remember(Point point)
        {
            sentPositions.Enqueue(point);
            while (sentPositions.Count > 16) sentPositions.Dequeue();
        }

        private bool SentRecently(Point point) =>
            sentPositions.Any(sent => Math.Abs(sent.X - point.X) <= 1 && Math.Abs(sent.Y - point.Y) <= 1);

        internal DemoDriver(string app, string ffmpeg, string work)
        {
            this.app = app;
            this.ffmpeg = ffmpeg;
            this.work = work;
            GetCursorPos(out originalCursor);
            cursor = originalCursor;
            // 送った入力には注入の印が付く。印のない入力は利用者の操作なので、録画を中止する。
            mouseHook = (code, message, data) =>
            {
                // SetCursorPosによる移動は注入の印が付かないことがあるため、送った位置への移動は除く。
                // 矢印キーやSpaceでSSCaptureがポインタを動かす間も、移動だけは除く。
                if (code >= 0 && (Marshal.ReadInt32(data, 12) & 0x3) == 0
                    && !(message == 0x0200 && Environment.TickCount64 < pointerMovedByApplicationUntil)
                    && !(message == 0x0200 && SentRecently(new Point(Marshal.ReadInt32(data, 0), Marshal.ReadInt32(data, 4)))))
                    physicalInput ??= $"マウスが操作されました（メッセージ 0x{message:x4}、位置 {Marshal.ReadInt32(data, 0)},{Marshal.ReadInt32(data, 4)}、送った位置 {cursor.X},{cursor.Y}）。";
                return CallNextHookEx(0, code, message, data);
            };
            keyboardHook = (code, message, data) =>
            {
                if (code >= 0 && (Marshal.ReadInt32(data, 8) & 0x10) == 0) physicalInput ??= "キーボードが操作されました。";
                return CallNextHookEx(0, code, message, data);
            };
            nint module = GetModuleHandle(null);
            mouseHandle = SetWindowsHookEx(14, mouseHook, module, 0);
            keyboardHandle = SetWindowsHookEx(13, keyboardHook, module, 0);
            if (mouseHandle == 0 || keyboardHandle == 0) throw new InvalidOperationException("入力の監視を開始できません。");
        }

        private string Raw(DemoScene scene) => Path.Combine(work, scene.Id + "-raw.mp4");

        internal async Task RecordAsync(DemoScene scene, DemoStage stage)
        {
            Console.WriteLine($"[{scene.Id}] {scene.Title}");
            labels.Clear();
            RecordArea = scene.Layout == DemoLayout.Scroll ? ScrollRecordArea : MainRecordArea;
            pointerMovedByApplicationUntil = 0;
            hiddenPanel = null;
            stage.Backdrop.Activate();
            stage.Arrange(scene.Layout);
            await Task.Delay(300);
            await StartRecorderAsync(Raw(scene));
            try { await scene.Run(this, stage); }
            catch
            {
                ReleaseInput();
                CancelCapture();
                throw;
            }
            finally { await StopRecorderAsync(); }
        }

        // 引数を省くと --capture-select（既定の入力プリセットの範囲選択）で起動する。
        internal async Task StartAsync(Point pointer, params string[] arguments)
        {
            await MoveAsync(pointer, 0);
            await WaitAsync(500);
            sceneStart = clock.Elapsed.TotalSeconds;
            sceneWallStart = DateTime.UtcNow;
            Mark();
            var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments.Length == 0 ? ["--capture-select"] : arguments) start.ArgumentList.Add(argument);
            Rectangle area = DemoPresentationArea;
            start.Environment[SSCapture.Capture.PresentationArea.EnvironmentVariable] = $"{area.X},{area.Y},{area.Width},{area.Height}";
            capture = Process.Start(start) ?? throw new InvalidOperationException("SSCaptureを起動できません。");
            captureOutput = capture.StandardOutput.ReadToEndAsync();
            captureError = capture.StandardError.ReadToEndAsync();
            var deadline = Stopwatch.StartNew();
            while (!SharedPresentationVisible(capture.Id))
            {
                if (capture.HasExited) throw new InvalidOperationException($"SSCaptureが撮影画面を出さずに終了しました（終了コード {capture.ExitCode}）。");
                if (deadline.ElapsedMilliseconds > 15000) throw new InvalidOperationException("撮影操作の共有表示が出ません。設定の「撮影操作を画面共有・配信に表示する」を確認してください。");
                await Task.Delay(50);
                Check(requireCapture: false);
            }
            await WaitAsync(400);
        }

        internal async Task FinishAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (capture is { HasExited: false })
            {
                if (deadline.ElapsedMilliseconds > 20000) throw new InvalidOperationException("撮影が終わりません。");
                await Task.Delay(50);
                if (physicalInput is not null) throw new DemoAbortException(physicalInput);
            }
            if (capture is { ExitCode: not 0 })
            {
                // 2は範囲やコントロールが選ばれずに終わったことを表す。場面の操作が意図どおりに働かなかったので失敗にする。
                string detail = (await captureOutput! + await captureError!).Trim();
                throw new InvalidOperationException($"SSCaptureが撮影せずに終了しました（終了コード {capture.ExitCode}）。{detail}");
            }
            capture?.Dispose();
            capture = null;
            sceneEnd = clock.Elapsed.TotalSeconds;
            // 修飾キーだけを押して離すと、スタートメニュー・メニューバー・入力言語の切り替えが動くことがある。
            // 割り当てのない仮想キーを1回挟んでから離す。
            if (keys.Count > 0) Neutral();
            ReleaseKeys();
            await Task.Delay(700);
        }

        internal async Task MoveAsync(Point target, int milliseconds)
        {
            int steps = Math.Max(1, milliseconds / 16);
            Point from = cursor;
            for (int step = 1; step <= steps; step++)
            {
                double t = step / (double)steps;
                double eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
                cursor = new Point(from.X + (int)Math.Round((target.X - from.X) * eased), from.Y + (int)Math.Round((target.Y - from.Y) * eased));
                Remember(cursor);
                if (!SetCursorPos(cursor.X, cursor.Y)) throw new InvalidOperationException("ポインタを移動できません。");
                if (steps > 1) await Task.Delay(16);
                Check();
            }
        }

        internal async Task WaitAsync(int milliseconds)
        {
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < milliseconds)
            {
                await Task.Delay(Math.Min(40, Math.Max(1, milliseconds - (int)wait.ElapsedMilliseconds)));
                Check();
            }
        }

        internal void Press(params DemoKey[] pressed)
        {
            Check();
            foreach (DemoKey key in pressed)
            {
                if (!keys.Add(key)) continue;
                keybd_event((byte)key, 0, key == DemoKey.Win ? 1u : 0u, 0);
                Thread.Sleep(30);
            }
            Mark();
        }

        // 撮影中に修飾キーを1つ離す。撮影画面が前面にあるため、単独で離してもメニューバー等は動かない。
        internal void Release(params DemoKey[] released)
        {
            Check();
            foreach (DemoKey key in released)
            {
                if (!keys.Remove(key)) continue;
                keybd_event((byte)key, 0, 2u | (key == DemoKey.Win ? 1u : 0u), 0);
                Thread.Sleep(30);
            }
            Mark();
        }

        // 矢印キーやSpaceを続けて押す。押している間は「Shift ＋ →」のように字幕に出す。
        // SSCaptureが実際のポインタを動かすキーでは、その移動を利用者の操作と誤らないよう監視から外し、終わった位置を読み直す。
        internal async Task TapKeyAsync(byte key, string name, int count, int interval, bool extended = false, bool movesPointer = false)
        {
            Check();
            Mark(name);
            for (int i = 0; i < count; i++)
            {
                if (movesPointer) pointerMovedByApplicationUntil = Environment.TickCount64 + interval + 400;
                keybd_event(key, 0, extended ? 1u : 0u, 0);
                await Task.Delay(40);
                keybd_event(key, 0, 2u | (extended ? 1u : 0u), 0);
                if (movesPointer)
                {
                    // SSCaptureが動かした先の位置も、遅れて届く通知と照合できるように覚える。
                    await Task.Delay(60);
                    GetCursorPos(out Point moved);
                    Remember(moved);
                }
                await WaitAsync(interval);
            }
            if (movesPointer)
            {
                await Task.Delay(150);
                GetCursorPos(out cursor);
                Remember(cursor);
            }
            await WaitAsync(250);
            Mark();
        }

        // ホイールを上へ1目盛りずつ回す。余白の調整ではSSCaptureがポインタを数px動かし、回し終えた後も動かすことがある。
        // そのため場面の終わりまでポインタの移動は監視から外す（クリックとキーの監視は続ける）。
        internal async Task WheelAsync(int notches, int interval, bool down = false)
        {
            Check();
            Mark(down ? "ホイール↓" : "ホイール↑");
            pointerMovedByApplicationUntil = long.MaxValue;
            for (int i = 0; i < notches; i++)
            {
                mouse_event(0x0800, 0, 0, down ? unchecked((uint)-120) : 120u, 0);
                await WaitAsync(interval);
            }
            await Task.Delay(150);
            GetCursorPos(out cursor);
            await WaitAsync(400);
            Mark();
        }

        internal async Task RightClickAsync()
        {
            Check();
            Mark("右クリック");
            mouse_event(0x0008, 0, 0, 0, 0);
            await Task.Delay(120);
            mouse_event(0x0010, 0, 0, 0, 0);
            await WaitAsync(900);
            Mark();
        }

        // スクロール撮影の操作窓は画面取得から常に除外され、録画に映らない。
        // 窓の位置を探して動画に枠と説明を重ね、「撮影を確定」ボタンの位置を返す。
        internal async Task<Rectangle> FindScrollConfirmAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 10000)
            {
                if (capture is null || capture.HasExited) break;
                nint button = 0, panel = 0;
                uint processId = (uint)capture.Id;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint owner);
                    if (owner != processId || !IsWindowVisible(window)) return true;
                    EnumChildWindows(window, (child, _) =>
                    {
                        var text = new StringBuilder(64);
                        _ = GetWindowText(child, text, text.Capacity);
                        if (text.ToString() != "撮影を確定") return true;
                        button = child;
                        return false;
                    }, 0);
                    if (button == 0) return true;
                    panel = window;
                    return false;
                }, 0);
                if (button != 0 && GetWindowRect(panel, out NativeRect panelRect) && GetWindowRect(button, out NativeRect buttonRect))
                {
                    hiddenPanel = Rectangle.FromLTRB(panelRect.Left, panelRect.Top, panelRect.Right, panelRect.Bottom);
                    hiddenPanelAt = clock.Elapsed.TotalSeconds;
                    return Rectangle.FromLTRB(buttonRect.Left, buttonRect.Top, buttonRect.Right, buttonRect.Bottom);
                }
                await WaitAsync(100);
            }
            throw new InvalidOperationException("スクロール撮影の操作窓が見つかりません。");
        }

        internal async Task MiddleClickAsync()
        {
            Check();
            Mark("中クリック");
            mouse_event(0x0020, 0, 0, 0, 0);
            await Task.Delay(120);
            mouse_event(0x0040, 0, 0, 0, 0);
            await WaitAsync(800);
            Mark();
        }

        internal async Task ClickAsync()
        {
            LeftDown();
            await Task.Delay(150);
            LeftUp();
        }

        internal void LeftDown()
        {
            Check();
            leftDown = true;
            mouse_event(0x0002, 0, 0, 0, 0);
            Mark();
        }

        internal void LeftUp()
        {
            if (!leftDown) return;
            leftDown = false;
            mouse_event(0x0004, 0, 0, 0, 0);
            Mark();
        }

        private void Neutral()
        {
            keybd_event(0xE8, 0, 0, 0);
            keybd_event(0xE8, 0, 2u, 0);
        }

        private void ReleaseKeys()
        {
            foreach (DemoKey key in keys.Reverse().ToArray())
            {
                keybd_event((byte)key, 0, 2u | (key == DemoKey.Win ? 1u : 0u), 0);
                keys.Remove(key);
            }
            Mark();
        }

        private void ReleaseInput()
        {
            LeftUp();
            if (keys.Count > 0) Neutral();
            ReleaseKeys();
        }

        private void CancelCapture()
        {
            if (capture is null) return;
            try
            {
                if (!capture.HasExited && !capture.WaitForExit(1500)) capture.Kill();
            }
            catch (InvalidOperationException) { }
            capture.Dispose();
            capture = null;
        }

        // 利用者の操作や、ほかの窓が前面に出たことを見つけたら中止する。
        private void Check(bool requireCapture = true)
        {
            if (physicalInput is not null) throw new DemoAbortException(physicalInput);
            if (!requireCapture || capture is null || capture.HasExited) return;
            nint foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint owner);
            if (foreground != 0 && owner != (uint)Environment.ProcessId && owner != (uint)capture.Id)
            {
                var className = new StringBuilder(128);
                _ = GetClassName(foreground, className, className.Capacity);
                string process;
                try { using var other = Process.GetProcessById((int)owner); process = other.ProcessName; }
                catch (ArgumentException) { process = owner.ToString(); }
                throw new DemoAbortException($"録画の対象ではない窓が前面に出ました（{process}、{className}）。");
            }
        }

        private void Mark(string? action = null)
        {
            var parts = new List<string>();
            foreach (DemoKey key in new[] { DemoKey.Ctrl, DemoKey.Shift, DemoKey.Alt, DemoKey.Win })
                if (keys.Contains(key)) parts.Add(key.ToString());
            if (leftDown) parts.Add("左クリック");
            if (action is not null) parts.Add(action);
            labels.Add((clock.Elapsed.TotalSeconds, string.Join(" ＋ ", parts)));
        }

        private static bool SharedPresentationVisible(int processId)
        {
            bool found = false;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner != (uint)processId || !IsWindowVisible(window)) return true;
                var title = new StringBuilder(256);
                _ = GetWindowText(window, title, title.Capacity);
                if (title.ToString().StartsWith("撮影操作の共有", StringComparison.Ordinal)) { found = true; return false; }
                return true;
            }, 0);
            return found;
        }

        private async Task StartRecorderAsync(string path)
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = work
            };
            foreach (string argument in new[]
            {
                "-hide_banner", "-y", "-f", "gdigrab", "-framerate", "30", "-draw_mouse", "1",
                "-offset_x", RecordArea.X.ToString(), "-offset_y", RecordArea.Y.ToString(),
                "-video_size", $"{RecordArea.Width}x{RecordArea.Height}", "-i", "desktop",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-pix_fmt", "yuv420p", path
            }) start.ArgumentList.Add(argument);
            recorder = Process.Start(start) ?? throw new InvalidOperationException("ffmpegを起動できません。");
            var ready = new TaskCompletionSource();
            var log = new StringBuilder();
            recorder.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (log) log.AppendLine(e.Data);
                if (e.Data.Contains("Press [q]", StringComparison.Ordinal) || e.Data.StartsWith("frame=", StringComparison.Ordinal)) ready.TrySetResult();
            };
            recorder.BeginErrorReadLine();
            _ = recorder.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(ready.Task, Task.Delay(10000)) != ready.Task)
                throw new InvalidOperationException("ffmpegの録画が始まりません。\n" + log);
            clock.Restart();
            await Task.Delay(300);
        }

        private async Task StopRecorderAsync()
        {
            if (recorder is null) return;
            try
            {
                await Task.Delay(300);
                if (!recorder.HasExited)
                {
                    recorder.StandardInput.Write("q");
                    recorder.StandardInput.Flush();
                }
                if (!recorder.WaitForExit(15000)) recorder.Kill();
            }
            finally
            {
                recorder.Dispose();
                recorder = null;
            }
        }

        // 字幕を重ねて、確認用のMP4と掲載用のGIFを作る。
        internal async Task RenderAsync(DemoScene scene, string gifOutput)
        {
            double from = Math.Max(0, sceneStart - 0.2), to = sceneEnd + 0.6;
            string directory = Path.Combine(work, scene.Id);
            Directory.CreateDirectory(directory);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(directory, "title.txt"), scene.Title, utf8);
            File.WriteAllText(Path.Combine(directory, "detail.txt"), scene.Detail, utf8);
            var filter = new StringBuilder("[0:v]");
            string Text(string file, int size, string color, string position, string? enable = null) =>
                $"drawtext=fontfile=font.ttc:textfile={scene.Id}/{file}:fontsize={size}:fontcolor=white:box=1:boxcolor={color}:boxborderw=12:{position}" +
                (enable is null ? "" : $":enable='{enable}'") + ",";
            filter.Append(Text("title.txt", 32, "0x1f2937@0.88", "x=24:y=18"));
            filter.Append(Text("detail.txt", 24, "0x1f2937@0.88", "x=24:y=76"));
            var segments = labels.Select((label, index) => (label.Label, Start: label.At, End: index + 1 < labels.Count ? labels[index + 1].At : to))
                .Where(segment => segment.Label.Length > 0 && segment.End > from).ToArray();
            for (int i = 0; i < segments.Length; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"key{i}.txt"), segments[i].Label, utf8);
                double a = Math.Max(0, segments[i].Start - from), b = segments[i].End - from;
                // 右下には撮影画面の操作ガイドが出るため、左下に置く。
                filter.Append(Text($"key{i}.txt", 32, "0xc2410c@0.94", "x=24:y=h-64",
                    $"between(t,{a.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)},{b.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)})"));
            }
            if (hiddenPanel is { } panel)
            {
                // 録画に映らない操作窓の位置に枠と説明を重ねる。
                File.WriteAllText(Path.Combine(directory, "panel.txt"), "操作窓（録画には映りません）", utf8);
                string shown = $"gte(t,{(hiddenPanelAt - from).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)})";
                filter.Append($"drawbox=x={panel.X - RecordArea.X}:y={panel.Y - RecordArea.Y}:w={panel.Width}:h={panel.Height}:color=0xf59e0b:t=4:enable='{shown}',");
                filter.Append(Text("panel.txt", 20, "0x1f2937@0.88", $"x={panel.X - RecordArea.X}:y={panel.Y - RecordArea.Y - 44}", shown));
            }
            filter.Length--;
            // どの場面も、撮影結果の画像を録画の後ろに約3秒つなげる。
            string result = new DirectoryInfo(Path.Combine(work, "captures")).EnumerateFiles("*.png")
                .Where(file => file.LastWriteTimeUtc >= sceneWallStart).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName
                ?? throw new InvalidOperationException("撮影結果の画像が見つかりません。");
            int viewWidth = RecordArea.Width - 40, viewHeight = RecordArea.Height - 100;
            Size resultSize;
            using (var image = Image.FromFile(result)) resultSize = image.Size;
            string resultName = CompositeScenes.Contains(scene.Id) ? "合成された1枚の画像" : "撮影した画像";
            File.WriteAllText(Path.Combine(directory, "result.txt"), $"{resultName}（{resultSize.Width}×{resultSize.Height}px）", utf8);
            string resultTitle = Text("result.txt", 28, "0x1f2937@0.88", "x=24:y=18").TrimEnd(',');
            // 縦長の結果（スクロール撮影）は縮めると読めないため、幅が収まれば等倍のまま上から下へ流して見せる。
            bool pan = resultSize.Height > viewHeight * 2 && resultSize.Width <= viewWidth;
            double resultSeconds = pan ? 4.5 : 3;
            string fit = pan
                ? $"crop=w=iw:h={viewHeight}:x=0:y='(ih-{viewHeight})*min(1,max(0,(t-0.6)/{(resultSeconds - 1.2).ToString(System.Globalization.CultureInfo.InvariantCulture)}))',"
                : $"scale=w={viewWidth}:h={viewHeight}:force_original_aspect_ratio=decrease,";
            // 合成結果の透明な部分が黒くならないよう、背景色の上に重ねる。
            string seconds = resultSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string captioned = filter + "[m];" +
                $"[1:v]{fit}format=rgba[img];color=c=0x34404f:s={RecordArea.Width}x{RecordArea.Height}:r=30:d={seconds}[bg];" +
                $"[bg][img]overlay=x=(W-w)/2:y=(H-h)/2+30:eof_action=repeat,setsar=1,fps=30,format=yuv420p,{resultTitle}[r];" +
                "[m]setsar=1,fps=30,format=yuv420p[mm];[mm][r]concat=n=2:v=1:a=0[c]";
            string mp4Script = Path.Combine(directory, "mp4.filter");
            File.WriteAllText(mp4Script, captioned, utf8);
            string gifScript = Path.Combine(directory, "gif.filter");
            File.WriteAllText(gifScript, captioned + ";[c]fps=12,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[g]", utf8);
            string seek = from.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string length = (to - from).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string[] inputs = ["-ss", seek, "-t", length, "-i", Raw(scene),
                "-loop", "1", "-framerate", "30", "-t", seconds, "-i", result];
            await RunFfmpegAsync(["-hide_banner", "-y", .. inputs, "-/filter_complex", mp4Script, "-map", "[c]",
                "-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", Path.Combine(work, scene.Id + ".mp4")]);
            string gif = Path.Combine(gifOutput, scene.Id + ".gif");
            await RunFfmpegAsync(["-hide_banner", "-y", .. inputs, "-/filter_complex", gifScript, "-map", "[g]", "-loop", "0", gif]);
            Console.WriteLine($"  {gif} {new FileInfo(gif).Length / 1024}KB");
        }

        // 入力の監視をこのスレッドで続けるため、変換の完了を待つ間もメッセージを止めない。
        private async Task RunFfmpegAsync(string[] arguments)
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = work };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpegを起動できません。");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("ffmpegの変換に失敗しました。\n" + await error);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReleaseInput();
            CancelCapture();
            if (recorder is { HasExited: false }) { try { recorder.Kill(); } catch (InvalidOperationException) { } }
            recorder?.Dispose();
            UnhookWindowsHookEx(mouseHandle);
            UnhookWindowsHookEx(keyboardHandle);
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }

        private delegate nint HookProc(int code, nint message, nint data);
        private delegate bool EnumWindowsProc(nint window, nint parameter);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint thread);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int length);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    }
}
