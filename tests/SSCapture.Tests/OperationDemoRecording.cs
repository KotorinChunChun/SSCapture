using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
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
    // タスクバーを映さないよう、主モニターの作業領域だけを録画する。幅と高さはH.264のため偶数にそろえる。
    private static Rectangle RecordArea => Screen.PrimaryScreen!.WorkingArea is var area ? area with { Width = area.Width & ~1, Height = area.Height & ~1 } : default;
    private const string DemoMarkerFile = "operation-demo.marker";
    private const int DemoSnapPixels = 100;

    private sealed record DemoScene(string Id, string Title, string Detail, Func<DemoDriver, DemoStage, Task> Run);

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
            // 既定の20pxでは吸着やグリッドの変化が動画で見分けにくいため、100pxにする。動画の説明にもこの値を書く。
            GridStepPixels = DemoSnapPixels,
            SnapDistancePixels = DemoSnapPixels
        }).ApplyDefaults();
        new SettingsStore(settingsPath).Save(settings);
    }

    private static DemoScene[] DemoScenes() =>
    [
        new("shift-monitor", "撮影前に Shift＋左クリック", "ポインタのあるモニター全体を撮影", async (d, s) =>
        {
            await d.StartAsync(new Point(980, 900));
            await d.WaitAsync(700);
            d.Press(DemoKey.Shift);
            await d.WaitAsync(1200);
            await d.ClickAsync();
            await d.FinishAsync();
        }),
        new("ctrl-control", "撮影前に Ctrl を押しながらクリック", "ポインタの下のボタンや入力欄を1つだけ撮影", async (d, s) =>
        {
            await d.StartAsync(s.Center(s.NameBox) + new Size(-120, -60));
            await d.WaitAsync(600);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(500);
            await d.MoveAsync(s.Center(s.NameBox), 500);
            await d.WaitAsync(800);
            await d.MoveAsync(s.Center(s.Notify), 600);
            await d.WaitAsync(800);
            await d.MoveAsync(s.Center(s.Send), 600);
            await d.WaitAsync(1000);
            await d.ClickAsync();
            await d.FinishAsync();
        }),
        new("ctrl-wheel-margin", "撮影前に Ctrl を押しながらホイール回転", "撮影対象の余白を広げる（1目盛りで四辺1pxずつ）", async (d, s) =>
        {
            await d.StartAsync(s.Center(s.Send) + new Size(-140, -70));
            await d.WaitAsync(600);
            d.Press(DemoKey.Ctrl);
            await d.WaitAsync(400);
            await d.MoveAsync(s.Center(s.Send), 600);
            await d.WaitAsync(900);
            await d.WheelAsync(24, 70);
            await d.WaitAsync(1200);
            await d.ClickAsync();
            await d.FinishAsync();
        }),
        new("ctrl-controls-drag", "撮影前に Ctrl を押しながら左ドラッグ", "通ったコントロールを元の位置関係のまま1枚にまとめる", async (d, s) =>
        {
            await d.StartAsync(s.Center(s.NameBox) + new Size(-120, -60));
            await d.WaitAsync(600);
            d.Press(DemoKey.Ctrl);
            await d.MoveAsync(s.Center(s.NameBox), 500);
            await d.WaitAsync(700);
            d.LeftDown();
            await d.MoveAsync(s.Center(s.Department), 600);
            await d.MoveAsync(s.Center(s.Notify), 700);
            await d.MoveAsync(s.Center(s.Send), 700);
            await d.WaitAsync(800);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("win-window", "撮影前に Win を押しながらクリック", "ポインタの下のウィンドウを撮影", async (d, s) =>
        {
            await d.StartAsync(new Point(1000, 900));
            await d.WaitAsync(600);
            d.Press(DemoKey.Win);
            await d.WaitAsync(400);
            await d.MoveAsync(s.Center(s.Entry) + new Size(0, 80), 700);
            await d.WaitAsync(1000);
            await d.MoveAsync(s.Center(s.Report), 800);
            await d.WaitAsync(1000);
            await d.ClickAsync();
            await d.FinishAsync();
        }),
        new("win-windows-drag", "撮影前に Win を押しながら左ドラッグ", "通ったウィンドウを位置と重なり順のまま1枚にまとめる", async (d, s) =>
        {
            await d.StartAsync(new Point(1000, 900));
            await d.WaitAsync(600);
            d.Press(DemoKey.Win);
            await d.WaitAsync(400);
            await d.MoveAsync(s.Center(s.Entry) + new Size(-150, 120), 700);
            await d.WaitAsync(900);
            d.LeftDown();
            await d.MoveAsync(s.Center(s.Entry) + new Size(200, 60), 700);
            await d.MoveAsync(s.Center(s.Report) + new Size(-100, 40), 900);
            await d.MoveAsync(s.Center(s.Report) + new Size(80, 0), 500);
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
            await d.MoveAsync(center + new Size(250, 120), 900);
            await d.MoveAsync(center + new Size(180, 170), 600);
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
            await d.MoveAsync(center + new Size(260, 90), 800);
            await d.MoveAsync(center + new Size(150, 180), 700);
            await d.MoveAsync(center + new Size(260, 160), 600);
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
            await d.MoveAsync(chart.Location + new Size(chart.Width / 3, chart.Height + 60), 900);
            await d.MoveAsync(chart.Location + new Size(chart.Width, chart.Height + 30), 800);
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
            await d.MoveAsync(chart.Location + new Size(chart.Width + 80, chart.Height / 3), 900);
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
            await d.MoveAsync(start + new Size(173, 117), 700);
            await d.WaitAsync(500);
            d.Press(DemoKey.Alt, DemoKey.Win);
            await d.WaitAsync(700);
            // グリッドの段差が見えるよう、ゆっくり大きく動かす。
            await d.MoveAsync(start + new Size(chart.Width - 20, chart.Height - 30), 2600);
            await d.WaitAsync(500);
            await d.MoveAsync(start + new Size(250, 160), 2200);
            await d.WaitAsync(500);
            await d.MoveAsync(start + new Size(390, 250), 1500);
            await d.WaitAsync(800);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("drag-alt-snap", "Alt を押しながらドラッグ", $"近くのウィンドウやモニターの端に吸着させる（この動画では{DemoSnapPixels}px以内）", async (d, s) =>
        {
            Rectangle report = s.Report.Bounds;
            Point nearStart = report.Location + new Size(-55, -45);
            Point nearEnd = new(report.Right + 55, report.Bottom + 45);
            await d.StartAsync(nearStart + new Size(-80, -60));
            await d.WaitAsync(600);
            await d.MoveAsync(nearStart, 600);
            d.Press(DemoKey.Alt);
            await d.WaitAsync(900);
            d.LeftDown();
            await d.MoveAsync(new Point(report.Right - 220, report.Bottom - 170), 900);
            await d.WaitAsync(400);
            await d.MoveAsync(nearEnd, 1100);
            await d.WaitAsync(1000);
            // Altを離すと吸着が外れて終点がポインタの位置に戻り、押し直すと再び吸着する。
            d.Release(DemoKey.Alt);
            await d.WaitAsync(1200);
            d.Press(DemoKey.Alt);
            await d.WaitAsync(1200);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("magnifier", "拡大鏡で画素単位に合わせる", "中クリックで拡大鏡を表示し、矢印キーで1pxずつ動かす", async (d, s) =>
        {
            Rectangle bar = s.Bar(3), next = s.Bar(4);
            await d.StartAsync(bar.Location + new Size(-7, -6));
            await d.WaitAsync(700);
            await d.MiddleClickAsync();
            await d.WaitAsync(900);
            await d.TapKeyAsync(0x27, "→", 7, 260, extended: true, movesPointer: true);
            await d.TapKeyAsync(0x28, "↓", 6, 260, extended: true, movesPointer: true);
            await d.WaitAsync(700);
            d.LeftDown();
            Point end = new(next.Right, s.Chart.Bottom);
            await d.MoveAsync(end + new Size(6, 5), 1200);
            await d.WaitAsync(500);
            await d.TapKeyAsync(0x25, "←", 6, 260, extended: true, movesPointer: true);
            await d.TapKeyAsync(0x26, "↑", 5, 260, extended: true, movesPointer: true);
            await d.WaitAsync(900);
            d.LeftUp();
            await d.FinishAsync();
        }),
        new("fixed-size", "固定サイズの枠を置いて撮影", "入力プリセット「固定サイズ 960×540」の枠を動かして撮影", async (d, s) =>
        {
            await d.StartAsync(new Point(150, 240), "--capture-preset", "--input-preset", "initial-input-fixed");
            await d.WaitAsync(800);
            // Shift＋矢印（10px移動）は、Shiftを押している間はモニター全体の選択表示になって枠が見えなくなるため、この動画では使わない。
            await d.MoveAsync(new Point(330, 300), 1000);
            await d.WaitAsync(600);
            await d.MoveAsync(new Point(380, 330), 800);
            await d.WaitAsync(700);
            // Spaceで基点を右上へ移す。もう一度押すと右下になり、画面右下の操作ガイドに重なってクリックが届かない。
            await d.TapKeyAsync(0x20, "Space", 1, 1200, movesPointer: true);
            await d.WheelAsync(12, 90);
            await d.WaitAsync(1000);
            await d.ClickAsync();
            await d.FinishAsync();
        })
    ];

    private enum DemoKey : byte { Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B }

    private sealed class DemoAbortException(string message) : Exception(message);

    // 全画面の架空のデスクトップ。録画中は前面に置き、利用者の窓を隠す。
    private sealed class DemoStage : IDisposable
    {
        internal readonly Form Backdrop, Entry, Report;
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
                Text = "申請フォーム（サンプル） - 業務アプリ", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(140, 220, 860, 600),
                FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Color.White
            };
            Label Caption(string text, int y) => new() { Text = text, AutoSize = true, Location = new Point(36, y + 4) };
            NameBox = new TextBox { Location = new Point(170, 36), Width = 380, Text = "山田 花子" };
            Department = new ComboBox { Location = new Point(170, 92), Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            Department.Items.AddRange(["営業部", "開発部", "総務部"]);
            Department.SelectedIndex = 1;
            Notes = new TextBox { Location = new Point(170, 148), Size = new Size(620, 180), Multiline = true, Text = "新機能の公開に合わせて、問い合わせ窓口の体制を見直します。" };
            Notify = new CheckBox { Text = "上長に通知する", AutoSize = true, Location = new Point(170, 350), Checked = true };
            Attach = new CheckBox { Text = "添付ファイルあり", AutoSize = true, Location = new Point(170, 392) };
            Send = new Button { Text = "送信", Location = new Point(540, 470), Size = new Size(120, 44) };
            Cancel = new Button { Text = "キャンセル", Location = new Point(672, 470), Size = new Size(120, 44) };
            Entry.Controls.AddRange([Caption("氏名", 36), NameBox, Caption("部署", 92), Department, Caption("内容", 148), Notes, Notify, Attach, Send, Cancel]);

            Report = new Form
            {
                Text = "週次レポート（サンプル） - 資料ビューアー", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(1130, 250, 700, 540),
                FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, BackColor = Color.White
            };
            chartPanel = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.White };
            chartPanel.Paint += (_, e) => PaintReport(e.Graphics, chartPanel.ClientSize);
            Report.Controls.Add(chartPanel);

            Backdrop.Load += (_, _) =>
            {
                Entry.Show(Backdrop);
                Report.Show(Backdrop);
            };
        }

        // 資料の窓のグラフの範囲（画面座標）。
        internal Rectangle Chart => chartPanel.RectangleToScreen(ChartArea(chartPanel.ClientSize));

        internal Point Center(Control control) => Center(control.RectangleToScreen(control.ClientRectangle));
        internal Point Center(Rectangle rectangle) => new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);

        // グラフの棒の範囲（画面座標）。拡大鏡の場面で、棒の角を狙うために使う。
        internal Rectangle Bar(int index)
        {
            Rectangle chart = ChartArea(chartPanel.ClientSize);
            int slot = chart.Width / BarValues.Length;
            return chartPanel.RectangleToScreen(new Rectangle(chart.Left + index * slot + slot / 4, chart.Bottom - BarValues[index], slot / 2, BarValues[index]));
        }

        private static readonly int[] BarValues = [120, 180, 150, 230, 260, 210];

        private static Rectangle ChartArea(Size client) => new(60, 90, client.Width - 120, 300);

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
                graphics.FillRectangle(bar, chart.Left + i * slot + slot / 4, chart.Bottom - values[i], slot / 2, values[i]);
                graphics.DrawString(labels[i], bodyFont, Brushes.DimGray, chart.Left + i * slot + slot / 2 - 9, chart.Bottom + 8);
            }
            graphics.DrawString("木・金は新機能の公開に伴い増加。", bodyFont, Brushes.DimGray, 36, client.Height - 56);
        }

        public void Dispose()
        {
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
        private Point cursor;
        private long pointerMovedByApplicationUntil;
        // 移動の通知は送った直後とは限らず、少し前に送った位置で届くことがあるため、直近の位置を覚えておく。
        private readonly Queue<Point> sentPositions = new();

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
            pointerMovedByApplicationUntil = 0;
            stage.Backdrop.Activate();
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
            Mark();
            var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments.Length == 0 ? ["--capture-select"] : arguments) start.ArgumentList.Add(argument);
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
                sentPositions.Enqueue(cursor);
                while (sentPositions.Count > 16) sentPositions.Dequeue();
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
                await WaitAsync(interval);
            }
            if (movesPointer)
            {
                await Task.Delay(150);
                GetCursorPos(out cursor);
            }
            await WaitAsync(250);
            Mark();
        }

        // ホイールを上へ1目盛りずつ回す。余白の調整ではSSCaptureがポインタを数px動かし、回し終えた後も動かすことがある。
        // そのため場面の終わりまでポインタの移動は監視から外す（クリックとキーの監視は続ける）。
        internal async Task WheelAsync(int notches, int interval)
        {
            Check();
            Mark("ホイール↑");
            pointerMovedByApplicationUntil = long.MaxValue;
            for (int i = 0; i < notches; i++)
            {
                mouse_event(0x0800, 0, 0, 120, 0);
                await WaitAsync(interval);
            }
            await Task.Delay(150);
            GetCursorPos(out cursor);
            await WaitAsync(400);
            Mark();
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
            if (leftDown) parts.Add("左ボタン");
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
                $"drawtext=fontfile=font.ttc:textfile={scene.Id}/{file}:fontsize={size}:fontcolor=white:box=1:boxcolor={color}:boxborderw=18:{position}" +
                (enable is null ? "" : $":enable='{enable}'") + ",";
            filter.Append(Text("title.txt", 46, "0x1f2937@0.88", "x=40:y=28"));
            filter.Append(Text("detail.txt", 34, "0x1f2937@0.88", "x=40:y=114"));
            var segments = labels.Select((label, index) => (label.Label, Start: label.At, End: index + 1 < labels.Count ? labels[index + 1].At : to))
                .Where(segment => segment.Label.Length > 0 && segment.End > from).ToArray();
            for (int i = 0; i < segments.Length; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"key{i}.txt"), segments[i].Label, utf8);
                double a = Math.Max(0, segments[i].Start - from), b = segments[i].End - from;
                // 右下には撮影画面の操作ガイドが出るため、左下に置く。
                filter.Append(Text($"key{i}.txt", 48, "0xc2410c@0.94", "x=40:y=h-110",
                    $"between(t,{a.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)},{b.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)})"));
            }
            filter.Length--;
            string captioned = filter + "[c]";
            string mp4Script = Path.Combine(directory, "mp4.filter");
            File.WriteAllText(mp4Script, captioned, utf8);
            string gifScript = Path.Combine(directory, "gif.filter");
            File.WriteAllText(gifScript, captioned + ";[c]fps=12,scale=960:-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[g]", utf8);
            string seek = from.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string length = (to - from).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            await RunFfmpegAsync(["-hide_banner", "-y", "-ss", seek, "-t", length, "-i", Raw(scene), "-/filter_complex", mp4Script, "-map", "[c]",
                "-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", Path.Combine(work, scene.Id + ".mp4")]);
            string gif = Path.Combine(gifOutput, scene.Id + ".gif");
            await RunFfmpegAsync(["-hide_banner", "-y", "-ss", seek, "-t", length, "-i", Raw(scene), "-/filter_complex", gifScript, "-map", "[g]", "-loop", "0", gif]);
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
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    }
}
