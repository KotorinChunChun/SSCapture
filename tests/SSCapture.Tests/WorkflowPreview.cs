using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    internal static int WorkflowPreview(string kind)
    {
        if (kind == "startup-settings")
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => [], startupRegistration: new FakeStartup());
            form.Shown += (_, _) =>
            {
                var work = Screen.FromControl(form).WorkingArea;
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = new Rectangle(work.Left + work.Width / 2, work.Top, work.Width - work.Width / 2, work.Height);
                form.SetStatus("自動起動の専用プレビューです。適用は模擬予約だけを変更し、Windowsの実予約・個人設定は変更しません。");
            };
            Application.Run(form);
            return 0;
        }
        if (kind == "ocr")
        {
            using var image = OcrFixture();
            using var form = new OcrNameForm(image, new AppSettings { FileNamePattern = "{yyyymmdd}_{hhmmss}_{ocr}.png" },
                CaptureIntent.Screenshot, new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)),
                (_, _, _) => Task.FromResult(new OcrResult(["PROJECT 2026", "東京 資料", "第三章"])));
            Application.Run(form);
            return 0;
        }
        if (kind == "settings-status")
        {
            using var form = new SettingsForm(new AppSettings { SaveDirectory = @"C:\Captures" }, _ => null, () => { }, () => []);
            using var checkTimer = new System.Windows.Forms.Timer { Interval = 100 };
            var elapsed = new System.Diagnostics.Stopwatch();
            bool checkedBefore = false, checkedAfter = false;
            string? error = null;
            form.Shown += (_, _) =>
            {
                elapsed.Start();
                form.SetStatus("コピーしました。\n保存しました: C:\\Captures\\20261010_210000_長い保存ファイル名でも文字を省略しない表示確認.png\n外部アプリへ送りました。");
                Console.WriteLine("STATUS READY");
                checkTimer.Start();
            };
            checkTimer.Tick += (_, _) =>
            {
                var status = form.Controls.Find("Status", true).Single();
                if (!checkedBefore && elapsed.Elapsed.TotalSeconds >= 59)
                {
                    checkedBefore = true;
                    if (status.Text.Length == 0) error = "59秒で結果が早く消えた";
                    else Console.WriteLine("PASS STATUS: result remains at 59 seconds");
                }
                if (!checkedAfter && elapsed.Elapsed.TotalSeconds >= 61.5)
                {
                    checkedAfter = true;
                    if (status.Text.Length != 0) error = "60秒後も古い結果が残った";
                    else Console.WriteLine("PASS STATUS: real timer cleared result after 60 seconds");
                }
                if (elapsed.Elapsed.TotalSeconds >= 66) form.Close();
            };
            Application.Run(form);
            if (error is not null || !checkedBefore || !checkedAfter)
            {
                Console.Error.WriteLine(error ?? "結果表示の検証が途中終了した");
                return 1;
            }
            return 0;
        }
        if (kind == "regions")
        {
            DisplayLayout[] displays = [new("fixture-monitor", "DISPLAY1（検証用）", new Rectangle(0, 0, 1920, 1080), 96, 96)];
            var saved = RegionCoordinates.FromBounds("本文", new Rectangle(120, 140, 1280, 720), displays,
                shortcut: "Win+Ctrl+F8");
            var last = RegionCoordinates.FromBounds("前回範囲", new Rectangle(240, 220, 960, 540), displays);
            using var form = new RegionManagerForm([saved], last, _ => Task.FromResult<Rectangle?>(null), _ => null, () => displays,
                (_, _) => Task.FromResult<Rectangle?>(null));
            Application.Run(form);
            return 0;
        }
        if (kind == "history")
        {
            using var history = new CaptureHistory();
            using var first = WorkflowImage("登録範囲のテスト画像", "本文　1280 × 720 px", new Size(1280, 720), Color.FromArgb(0, 104, 116));
            using var second = WorkflowImage("スクリーンショットのテスト画像", "全画面　960 × 540 px", new Size(960, 540), Color.FromArgb(66, 86, 154));
            history.Add(first, null, new DateTimeOffset(2026, 10, 10, 14, 25, 10, TimeSpan.FromHours(9)));
            history.Add(second, null, new DateTimeOffset(2026, 10, 10, 14, 26, 35, TimeSpan.FromHours(9)));
            using var form = new HistoryForm(history, _ => Task.FromResult("検証用のため出力しません。"),
                _ => Task.FromResult("検証用のため出力しません。"));
            Application.Run(form);
            return 0;
        }
        throw new ArgumentException("プレビューの種類は regions / history / ocr / settings-status です。", nameof(kind));
    }

    private static Bitmap WorkflowImage(string title, string subtitle, Size size, Color accent)
    {
        var image = new Bitmap(size.Width, size.Height);
        using Graphics graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(245, 248, 251));
        using var color = new SolidBrush(accent);
        using var heading = new Font("Yu Gothic UI", 28F, FontStyle.Bold);
        using var body = new Font("Yu Gothic UI", 22F);
        graphics.FillRectangle(color, 0, 0, image.Width, 14);
        graphics.DrawString("SSCapture SAMPLE", heading, color, 40, 40);
        graphics.DrawString(title, body, Brushes.Black, 40, 116);
        graphics.DrawString(subtitle, body, Brushes.DimGray, 40, 168);
        graphics.DrawString("合成画像のみ・保存OFF", body, Brushes.DimGray, 40, 220);
        for (int index = 0; index < 5; index++)
            graphics.FillRectangle(color, 50 + index * 120, image.Height - 60 - (index + 1) * 35, 76, (index + 1) * 35);
        return image;
    }
}
