using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

// READMEに載せる「撮影中の画面」の画像を生成する。
// 背景は架空のデスクトップ（設定画面一覧の画像とサンプルの資料の窓）で、このPCの実際の画面は取得しない。
// マウス操作はSessionControllerへ直接渡し、実際のマウス・キーは使わない。
// 撮影画面と操作ガイドはDrawToBitmapで自分自身を描かせる。オーバーレイは画面取得から除外されているため、画面からは取得しない。
internal static partial class Program
{
    private static readonly Rectangle SceneMonitor = new(0, 0, 1920, 1080);
    private static readonly Rectangle SceneSettingsWindow = new(80, 60, 1037, 719);
    private static readonly Rectangle SceneReportWindow = new(1170, 230, 680, 520);

    internal static int CaptureSceneGallery(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        string? settingsImage = FindRepositoryFile(Path.Combine("docs", "images", "settings", "01-basic.png"));
        if (settingsImage is null)
        {
            Console.Error.WriteLine("docs/images/settings/01-basic.png が見つかりません。リポジトリのルートで実行し、先に --settings-gallery で設定画面の画像を作ってください。");
            return 2;
        }
        Application.SetDefaultFont(new Font("Yu Gothic UI", 12F));
        using var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        int exitCode = 1;
        host.Shown += async (_, _) =>
        {
            try
            {
                await CaptureScenesAsync(output, settingsImage);
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { host.Close(); }
        };
        Application.Run(host);
        return exitCode;
    }

    private static string? FindRepositoryFile(string relative)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task CaptureScenesAsync(string output, string settingsImage)
    {
        AppSettings settings = new AppSettings().ApplyDefaults() with
        {
            ShowMagnifier = true, ShowPointerGuide = true, ShowSelectionGuide = true, DimCaptureBackground = true,
            LimitToStartingMonitor = false
        };
        // 1点目を決めた後の矩形ドラッグ。資料のグラフを囲んでいる途中。
        await CaptureSceneAsync(Path.Combine(output, "capture-scene-drag.png"), settingsImage, settings, CaptureMode.Rectangle, null,
            new Point(1215, 330), session =>
            {
                var start = new Point(1215, 330);
                var end = new Point(1745, 668);
                session.MouseDown(SceneOverlay(session), MouseButtons.Left, start);
                MoveInSteps(session, start, end);
                return end;
            });
        // 2回クリックの方式で、1点目をクリックして終点を待っている状態。
        await CaptureSceneAsync(Path.Combine(output, "capture-scene-two-clicks.png"), settingsImage,
            settings with { SelectionMethod = CaptureSelectionMethod.TwoClicks }, CaptureMode.Rectangle, null,
            new Point(250, 135), session =>
            {
                var start = new Point(250, 135);
                var end = new Point(770, 425);
                session.MouseDown(SceneOverlay(session), MouseButtons.Left, start);
                session.MouseUp(MouseButtons.Left, start);
                MoveInSteps(session, start, end);
                return end;
            });
        // ウィンドウ撮影で、資料の窓を指している状態。拡大鏡に棒グラフの角が映る位置を指す。
        await CaptureSceneAsync(Path.Combine(output, "capture-scene-window.png"), settingsImage, settings, CaptureMode.Window, null,
            new Point(1534, 424), session => new Point(1534, 424));
        // 矩形モードでウィンドウ上の左ボタンを押したまま、ホイールで外周を広げた状態。
        await CaptureSceneAsync(Path.Combine(output, "capture-scene-window-margin.png"), settingsImage, settings, CaptureMode.Rectangle, null,
            new Point(1534, 424), session =>
            {
                var pointer = new Point(1534, 424);
                session.MouseDown(SceneOverlay(session), MouseButtons.Left, pointer);
                session.MouseWheel(pointer, 120 * 12);
                return pointer;
            });
        // 固定サイズ960×540pxの枠を置く位置を選んでいる状態。
        await CaptureSceneAsync(Path.Combine(output, "capture-scene-fixed.png"), settingsImage, settings, CaptureMode.FixedRegion, new Size(960, 540),
            new Point(150, 120), session => new Point(150, 120));
    }

    private static async Task CaptureSceneAsync(string path, string settingsImage, AppSettings settings, CaptureMode mode,
        Size? fixedSize, Point initialPointer, Func<SessionController, Point> operate)
    {
        DesktopSnapshot snapshot = new(SceneDesktop(settingsImage), SceneMonitor, [SceneMonitor]);
        var options = new CaptureOptions(mode, CaptureIntent.Screenshot, OutputDestination.Clipboard, FreezeFrame: true,
            FixedSize: fixedSize, LimitToStartingMonitor: false, ShowSelectionGuide: true, OutputSettings: settings with { DefaultMode = mode },
            DimCaptureBackground: true);
        var session = new SessionController(options, snapshot, [SceneSettingsWindow, SceneReportWindow],
            initialPointer: initialPointer, confineCursor: _ => new MemoryStream());
        try
        {
            session.Start();
            // 撮影画面の16msタイマーは実際のポインタと修飾キーを読む。画像に実際のマウス位置が混ざらないよう止める。
            // 止めている間は、MoveToなどの操作のたびに描画の更新を直接行う。
            var timer = (System.Windows.Forms.Timer?)typeof(SessionController)
                .GetField("snapTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session);
            timer?.Stop();
            session.MoveTo(initialPointer);
            await SceneRenderAsync();
            Point pointer = operate(session);
            await SceneRenderAsync();
            // 表示や前面化に伴って実際のポインタ位置のマウス移動が届くことがあるため、描く直前に指定の位置へ戻す。
            session.MoveTo(pointer);
            using var image = new Bitmap(SceneMonitor.Width, SceneMonitor.Height, PixelFormat.Format32bppArgb);
            CaptureOverlay overlay = SceneOverlay(session);
            overlay.DrawToBitmap(image, new Rectangle(Point.Empty, SceneMonitor.Size));
            using (var graphics = Graphics.FromImage(image))
            {
                foreach (var guide in Application.OpenForms.OfType<SelectionGuideForm>().Where(form => form.Visible))
                {
                    using var guideImage = new Bitmap(guide.Width, guide.Height, PixelFormat.Format32bppArgb);
                    guide.DrawToBitmap(guideImage, new Rectangle(Point.Empty, guide.Size));
                    graphics.DrawImage(guideImage, guide.Left - SceneMonitor.Left, guide.Top - SceneMonitor.Top);
                }
                Cursors.Default.Draw(graphics, new Rectangle(pointer.X - SceneMonitor.Left, pointer.Y - SceneMonitor.Top,
                    Cursors.Default.Size.Width, Cursors.Default.Size.Height));
            }
            image.Save(path, ImageFormat.Png);
            Console.WriteLine($"{Path.GetFileName(path)} {image.Width}x{image.Height}");
        }
        finally
        {
            session.Cancel();
            using CaptureOutcome outcome = await session.Completion;
            await SceneRenderAsync();
        }
    }

    private static CaptureOverlay SceneOverlay(SessionController session) =>
        Application.OpenForms.OfType<CaptureOverlay>().Single(form => form.SelectionMenu == session.SelectionMenu && form.Visible);

    private static void MoveInSteps(SessionController session, Point from, Point to)
    {
        const int steps = 12;
        for (int step = 1; step <= steps; step++)
            session.MoveTo(new Point(from.X + (to.X - from.X) * step / steps, from.Y + (to.Y - from.Y) * step / steps));
    }

    private static async Task SceneRenderAsync()
    {
        for (int i = 0; i < 4; i++)
        {
            Application.DoEvents();
            await Task.Delay(120);
        }
    }

    // 架空のデスクトップ。壁紙の上に、設定画面と架空の資料の窓を置く。
    private static Bitmap SceneDesktop(string settingsImage)
    {
        var desktop = new Bitmap(SceneMonitor.Width, SceneMonitor.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(desktop);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var wallpaper = new LinearGradientBrush(new Rectangle(Point.Empty, SceneMonitor.Size),
            Color.FromArgb(214, 228, 240), Color.FromArgb(160, 190, 222), LinearGradientMode.ForwardDiagonal))
            graphics.FillRectangle(wallpaper, new Rectangle(Point.Empty, SceneMonitor.Size));

        DrawSceneShadow(graphics, SceneSettingsWindow);
        using (var settings = Image.FromFile(settingsImage))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(settings, SceneSettingsWindow);
        }

        DrawSceneShadow(graphics, SceneReportWindow);
        var window = SceneReportWindow;
        graphics.FillRectangle(Brushes.White, window);
        using (var titleBar = new SolidBrush(Color.FromArgb(243, 243, 243)))
            graphics.FillRectangle(titleBar, window.X, window.Y, window.Width, 36);
        using var titleFont = new Font("Yu Gothic UI", 11F);
        using var headingFont = new Font("Yu Gothic UI", 18F, FontStyle.Bold);
        using var bodyFont = new Font("Yu Gothic UI", 11F);
        graphics.DrawString("週次レポート（サンプル） - 資料ビューアー", titleFont, Brushes.Black, window.X + 14, window.Y + 8);
        graphics.DrawString("問い合わせ件数の推移", headingFont, Brushes.Black, window.X + 36, window.Y + 60);
        Rectangle chart = new(window.X + 60, window.Y + 120, window.Width - 120, 300);
        using (var axis = new Pen(Color.FromArgb(150, 150, 150), 2))
        {
            graphics.DrawLine(axis, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
            graphics.DrawLine(axis, chart.Left, chart.Top, chart.Left, chart.Bottom);
        }
        int[] values = [120, 180, 150, 230, 260, 210];
        string[] labels = ["月", "火", "水", "木", "金", "土"];
        Color[] colors = [Color.FromArgb(76, 140, 200), Color.FromArgb(76, 140, 200), Color.FromArgb(76, 140, 200),
            Color.FromArgb(230, 126, 60), Color.FromArgb(230, 126, 60), Color.FromArgb(76, 140, 200)];
        int slot = chart.Width / values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            using var bar = new SolidBrush(colors[i]);
            graphics.FillRectangle(bar, chart.Left + i * slot + slot / 4, chart.Bottom - values[i], slot / 2, values[i]);
            graphics.DrawString(labels[i], bodyFont, Brushes.DimGray, chart.Left + i * slot + slot / 2 - 9, chart.Bottom + 8);
        }
        graphics.DrawString("木・金は新機能の公開に伴い増加。", bodyFont, Brushes.DimGray, window.X + 36, window.Bottom - 56);
        using (var border = new Pen(Color.FromArgb(200, 200, 200)))
            graphics.DrawRectangle(border, window.X, window.Y, window.Width - 1, window.Height - 1);
        return desktop;
    }

    private static void DrawSceneShadow(Graphics graphics, Rectangle window)
    {
        for (int i = 1; i <= 8; i++)
        {
            using var shadow = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            graphics.FillRectangle(shadow, Rectangle.Inflate(window, i, i) with { Y = window.Y - i + 4 });
        }
    }
}
