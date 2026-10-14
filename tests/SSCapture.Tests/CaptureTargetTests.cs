using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunCaptureTargetToastSmoke()
    {
        try
        {
            using var toast = new CaptureToastForm();
            foreach (string target in new[] { "デスクトップ【1】全体", "デスクトップ【1】の 【100,100】 ～ 【200,200】" })
            {
                var delivery = new CaptureDelivery(CaptureIntent.Screenshot, (OutputDestination)7, OutputDestination.None, CaptureDescription: target);
                Check(toast.TryShow(delivery.Message, false, NotificationCorner.BottomRight, out string? warning), warning ?? "通知を表示できない");
                Application.DoEvents();
                Check(FeedbackNative.IsWindowVisible(toast.Handle), "実通知が表示されない");
                Check(FeedbackNative.GetWindowDisplayAffinity(toast.Handle, out uint affinity) && affinity == 0x11, "画面取得除外がない");
                var label = toast.Controls.Find("CaptureToastMessage", true).Single();
                Size measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Check(measured.Height <= label.Height, "具体的な通知が見切れる");
                Thread.Sleep(200);
                toast.Hide();
                Console.WriteLine("PASS TOAST " + target + " 複数出力の実表示・折返し・画面取得除外");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL TOAST " + ex.Message); return 1; }
    }

    private static readonly DisplayTarget[] TargetDisplays =
    [new("upper", new(-200, -300, 500, 300)), new("main", new(0, 0, 400, 300), true), new("right", new(400, 0, 300, 200))];

    private static void CaptureTargetTests()
    {
        Run("通知は即時撮影と同じ番号で全体・ローカル座標を具体化", () =>
        {
            Equal("デスクトップ【1】全体", CaptureTarget.Describe(new(0, 0, 400, 300), TargetDisplays));
            Equal("デスクトップ【1】の 【100,100】 ～ 【200,200】", CaptureTarget.Describe(new(100, 100, 100, 100), TargetDisplays));
            Equal("デスクトップ【2】全体", CaptureTarget.Describe(new(-200, -300, 500, 300), TargetDisplays));
            Equal("デスクトップ【2】の 【100,100】 ～ 【200,200】", CaptureTarget.Describe(new(-100, -200, 100, 100), TargetDisplays));
            Equal("デスクトップ【3】全体", CaptureTarget.Describe(new(400, 0, 300, 200), TargetDisplays.Reverse()));
            Equal("デスクトップ【1・3】にまたがる範囲（全体座標 【350,50】 ～ 【450,150】）", CaptureTarget.Describe(new(350, 50, 100, 100), TargetDisplays));
            Equal("デスクトップ【1・2・3】全体", CaptureTarget.Describe(new(-200, -300, 900, 600), TargetDisplays));
            Equal<string?>(null, CaptureTarget.Describe(new(9000, 9000, 100, 100), TargetDisplays));
        });
        Run("通知は指定範囲と成功/失敗・録画を明記しパスを含めない", () =>
        {
            var delivery = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None,
                SavedPath: "非公開パス.png", CaptureDescription: "デスクトップ【1】全体");
            Equal("デスクトップ【1】全体をコピーしました", delivery.Message);
            Equal("デスクトップ【1】の 【100,100】 ～ 【200,200】 をコピーしました", (delivery with { CaptureDescription = "デスクトップ【1】の 【100,100】 ～ 【200,200】" }).Message);
            Equal("デスクトップ【1】全体の動画を保存しました", (delivery with { Intent = CaptureIntent.Recording, Completed = OutputDestination.File }).Message);
            Equal("デスクトップ【1】全体をコピーしました" + Environment.NewLine + "デスクトップ【1】全体を外部アプリへ送れませんでした",
                (delivery with { Failed = OutputDestination.ExternalApp }).Message);
            Check(!delivery.Message.Contains("非公開"), "保存パスを表示した");
        });
        Run("通知対象は配送開始時の配置で固定し画面読取失敗も出力を妨げない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var bitmap = OutputImage();
            IReadOnlyList<DisplayTarget> displays = TargetDisplays;
            int reads = 0; CaptureDelivery? delivery = null;
            var copy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => copy.Task, delivered: value => delivery = value,
                readDisplays: () => { reads++; return displays; });
            var operation = router.ScreenshotAsync(bitmap, OutputDestination.Clipboard, OutputSettings(directory), captureBounds: new(100, 100, 100, 100));
            displays = [new("changed", new(100, 100, 400, 300), true)]; copy.SetResult(); await operation;
            Equal(1, reads); Equal("デスクトップ【1】の 【100,100】 ～ 【200,200】 をコピーしました", delivery!.Message);
            router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask, delivered: value => delivery = value,
                readDisplays: () => throw new InvalidOperationException("表示列挙失敗"));
            await router.ScreenshotAsync(bitmap, OutputDestination.Clipboard, OutputSettings(directory), captureBounds: new(0, 0, 100, 100));
            Equal("コピーが完了しました", delivery!.Message);
        })));
        Run("具体的な通知は長い座標と複数出力でも折返しをDPI別に収める", () =>
        {
            var delivery = new CaptureDelivery(CaptureIntent.Screenshot, (OutputDestination)7, OutputDestination.None,
                CaptureDescription: "デスクトップ【1・2】にまたがる範囲（全体座標 【-1920,-1440】 ～ 【3440,1440】）");
            foreach (int dpi in new[] { 96, 144, 192 })
            {
                Rectangle work = new(-2560, -200, 2560, 1440);
                using var form = new CaptureToastForm(_ => new(true, null), _ => { }, () => (work, dpi));
                Check(form.TryShow(delivery.Message, false, NotificationCorner.BottomRight, out _), "通知を生成できない");
                foreach (Control child in Descendants(form)) _ = child.Handle; LayoutTree(form);
                var label = form.Controls.Find("CaptureToastMessage", true).Single();
                Size measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Check(measured.Height <= label.Height && work.Contains(form.Bounds), $"DPI{dpi}で文字か通知が見切れる");
            }
        });
    }
}
