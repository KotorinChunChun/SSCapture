using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void DeliveryTests()
    {
        foreach (CaptureIntent intent in Enum.GetValues<CaptureIntent>())
        foreach (int mask in Enumerable.Range(0, 8))
        {
            OutputDestination destination = (OutputDestination)mask;
            Run($"完了通知: {intent}/{destination}の実出力と1回通知", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage();
                string source = DeliverySource(directory);
                string editor = CreateFakeEditor(directory);
                var settings = OutputSettings(directory) with { ImageEditorPath = editor, VideoEditorPath = editor };
                List<CaptureDelivery> received = [];
                List<string> order = [];
                var bounds = new Rectangle(-1600, 120, 1280, 720);
                int copies = 0, launches = 0;
                var router = new OutputRouter(Path.Combine(directory, "work"),
                    _ => { copies++; return Task.CompletedTask; },
                    _ => { copies++; return Task.CompletedTask; },
                    _ => launches++,
                    captured: (_, _, _) => order.Add("history"),
                    delivered: result => { order.Add("notice"); received.Add(result); });
                if (intent == CaptureIntent.Screenshot)
                    await router.ScreenshotAsync(image, destination, settings, onDelivered: () => order.Add("region"), captureBounds: bounds);
                else await router.RecordingAsync(source, destination, settings, captureBounds: bounds);
                Equal(destination.HasFlag(OutputDestination.Clipboard) ? 1 : 0, copies);
                Equal(destination.HasFlag(OutputDestination.ExternalApp) ? 1 : 0, launches);
                Equal(destination == OutputDestination.None ? 0 : 1, received.Count);
                if (destination == OutputDestination.None)
                {
                    Equal(0, order.Count);
                    return;
                }
                var notification = received.Single();
                Equal(intent, notification.Intent); Equal(destination, notification.Completed);
                Equal<Rectangle?>(bounds, notification.CaptureBounds);
                Equal(OutputDestination.None, notification.Failed);
                Check(notification.HasSuccess && !notification.HasFailure, "成功/失敗状態が一致しない");
                Check(!notification.Message.Contains(directory, StringComparison.Ordinal), "通知に保存先が混入");
                Equal(intent == CaptureIntent.Screenshot ? "history,region,notice" : "notice", string.Join(',', order));
                if (destination.HasFlag(OutputDestination.File))
                {
                    string saved = Directory.GetFiles(settings.SaveDirectory).Single();
                    Equal(saved, notification.SavedPath);
                    Check(File.ReadAllBytes(saved).Length > 0, "通知時に保存が完了していない");
                    Check(notification.Message.Contains(intent == CaptureIntent.Screenshot ? "スクリーンショットを保存しました" : "動画を保存しました", StringComparison.Ordinal), "保存対象を区別していない");
                }
                else Equal<string?>(null, notification.SavedPath);
                if (mask == 7) Equal(3, notification.Message.Split(Environment.NewLine).Length);
            })));
        }

        foreach (CaptureIntent intent in Enum.GetValues<CaptureIntent>())
        foreach (OutputDestination fail in new[] { OutputDestination.Clipboard, OutputDestination.File, OutputDestination.ExternalApp, (OutputDestination)7 })
        {
            Run($"完了通知: {intent}/{fail}失敗でも実成功を保持して例外前に通知", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage();
                string source = DeliverySource(directory), editor = CreateFakeEditor(directory);
                var settings = OutputSettings(directory) with { ImageEditorPath = editor, VideoEditorPath = editor };
                if (fail.HasFlag(OutputDestination.File)) File.WriteAllText(settings.SaveDirectory, "blocking file");
                const string privateOcr = "機密OCR文字列";
                string privateError = $"{directory} {privateOcr}";
                Task Copy() => fail.HasFlag(OutputDestination.Clipboard)
                    ? Task.FromException(new IOException(privateError)) : Task.CompletedTask;
                List<CaptureDelivery> received = [];
                int remembered = 0, kept = 0;
                var router = new OutputRouter(Path.Combine(directory, "work"), _ => Copy(), _ => Copy(),
                    _ => { if (fail.HasFlag(OutputDestination.ExternalApp)) throw new IOException(privateError); },
                    captured: (_, _, _) => kept++, delivered: received.Add);
                bool failed = false;
                try
                {
                    if (intent == CaptureIntent.Screenshot)
                        await router.ScreenshotAsync(image, (OutputDestination)7, settings, onDelivered: () => remembered++);
                    else await router.RecordingAsync(source, (OutputDestination)7, settings);
                }
                catch (IOException)
                {
                    failed = true;
                    Equal(1, received.Count);
                }
                Check(failed, "従来の出力例外が失われた");
                var notification = received.Single();
                OutputDestination expectedFailed = fail.HasFlag(OutputDestination.File) ? fail | OutputDestination.ExternalApp : fail;
                Equal(expectedFailed, notification.Failed);
                Equal((OutputDestination)7 & ~expectedFailed, notification.Completed);
                Equal(expectedFailed.HasFlag(OutputDestination.File) ? null : Directory.GetFiles(settings.SaveDirectory).Single(), notification.SavedPath);
                Equal<Rectangle?>(null, notification.CaptureBounds);
                Check(notification.HasFailure && !notification.FolderOpenFailed, "出力失敗をフォルダ表示失敗と混同");
                Equal(notification.Completed != OutputDestination.None, notification.HasSuccess);
                Check(!notification.Message.Contains(directory, StringComparison.Ordinal) && !notification.Message.Contains(privateOcr, StringComparison.Ordinal), "個人情報が通知へ混入");
                Equal(intent == CaptureIntent.Screenshot ? 1 : 0, kept);
                Equal(intent == CaptureIntent.Screenshot && notification.HasSuccess ? 1 : 0, remembered);
            })));
        }

        foreach (CaptureIntent intent in Enum.GetValues<CaptureIntent>())
        {
            Run($"完了通知: {intent}保存後フォルダだけ失敗しても保存成功を維持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage(); string source = DeliverySource(directory);
                var settings = OutputSettings(directory) with { OpenFolderAfterSave = true };
                List<CaptureDelivery> received = [];
                var router = new OutputRouter(Path.Combine(directory, "work"), launch: _ => throw new IOException("folder launch failed"), delivered: received.Add);
                bool failed = false;
                try { await DeliverSample(router, intent, image, source, OutputDestination.File, settings); }
                catch (IOException) { failed = true; Equal(1, received.Count); }
                Check(failed, "フォルダ起動エラーを従来の呼び出し元へ返さない");
                var notification = received.Single();
                Equal(new CaptureDelivery(intent, OutputDestination.File, OutputDestination.None, true,
                    Directory.GetFiles(settings.SaveDirectory).Single()), notification with { CaptureSettings = null });
                Check(notification.HasSuccess && notification.HasFailure, "保存成功とフォルダ失敗は両立する");
                Check(notification.Message.Contains("保存フォルダを開けませんでした", StringComparison.Ordinal), "フォルダ失敗の明示なし");
                Equal(1, Directory.GetFiles(settings.SaveDirectory).Length);
            })));
            Run($"完了通知: {intent}は衝突回避後の保存先を保持し表示文言へ混入しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage(); string source = DeliverySource(directory);
                var settings = OutputSettings(directory);
                List<CaptureDelivery> received = [];
                var router = new OutputRouter(Path.Combine(directory, "work"), delivered: received.Add);
                var bounds = new Rectangle(1800, -720, 1280, 720);
                var plan = new FileNamePlan("私的OCR 名前,{##}.png", DateTimeOffset.Now, intent);
                Directory.CreateDirectory(settings.SaveDirectory);
                string first = Path.Combine(settings.SaveDirectory, plan.Candidate(0));
                string saved = Path.Combine(settings.SaveDirectory, plan.Candidate(1));
                File.WriteAllBytes(first, [17]);
                if (intent == CaptureIntent.Screenshot)
                    await router.ScreenshotAsync(image, OutputDestination.File, settings, namePlan: plan, captureBounds: bounds);
                else await router.RecordingAsync(source, OutputDestination.File, settings, namePlan: plan, captureBounds: bounds);
                var notification = received.Single();
                Equal(saved, notification.SavedPath); Equal<Rectangle?>(bounds, notification.CaptureBounds);
                Check(File.ReadAllBytes(saved).Length > 0, "確定パスが保存済みファイルを示していない");
                Check(!notification.Message.Contains("私的OCR", StringComparison.Ordinal)
                    && !notification.Message.Contains(directory, StringComparison.Ordinal), "通知文言に保存パスが混入した");
            })));
            Run($"完了通知: {intent}通知observerの例外で保存成功を失敗にしない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage(); string source = DeliverySource(directory);
                var settings = OutputSettings(directory);
                int calls = 0; CaptureDelivery? observed = null;
                var router = new OutputRouter(Path.Combine(directory, "work"), delivered: result =>
                {
                    calls++; observed = result;
                    throw new InvalidOperationException("synthetic UI observer failure");
                });
                string result = await DeliverSample(router, intent, image, source, OutputDestination.File, settings);
                Equal(1, calls); Equal(1, Directory.GetFiles(settings.SaveDirectory).Length);
                Equal(OutputDestination.File, observed!.Completed); Equal(OutputDestination.None, observed.Failed);
                Check(result.Contains("保存しました:", StringComparison.Ordinal), "保存の戻り値を変更した");
            })));
            Run($"完了通知: {intent}コピー応答待ちでは通知せず完了後1回のみ", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage(); string source = DeliverySource(directory);
                var pendingCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                List<CaptureDelivery> received = [];
                var router = new OutputRouter(Path.Combine(directory, "work"), _ => pendingCopy.Task, _ => pendingCopy.Task, delivered: received.Add);
                Task<string> operation = DeliverSample(router, intent, image, source, OutputDestination.Clipboard, OutputSettings(directory));
                Equal(0, received.Count); Check(!operation.IsCompleted, "テスト用コピー待ちを通過した");
                pendingCopy.SetResult(); await operation;
                Equal(1, received.Count); Equal(OutputDestination.Clipboard, received[0].Completed);
            })));
        }

        Run("完了通知: 固定文言は成功・失敗・出力OFFを正確に表す", () =>
        {
            Equal("コピーが完了しました", new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None).Message);
            Equal("スクリーンショットを保存しました", new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None).Message);
            Equal("動画を保存しました", new CaptureDelivery(CaptureIntent.Recording, OutputDestination.File, OutputDestination.None).Message);
            Equal("外部アプリへ送りました", new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.ExternalApp, OutputDestination.None).Message);
            Equal("コピーできませんでした", new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.Clipboard).Message);
            Equal("保存できませんでした", new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.File).Message);
            Equal("外部アプリへ送れませんでした", new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.ExternalApp).Message);
            var none = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.None);
            Check(!none.HasSuccess && !none.HasFailure, "出力OFFを処理済みと扱った"); Equal("", none.Message);
        });
        Run("完了通知: 失敗通知observerがthrowしても本来のコピー例外を維持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage(); int calls = 0;
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.FromException(new IOException("original-copy-error")),
                delivered: _ => { calls++; throw new Exception("observer-error"); });
            string? error = null;
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory)); }
            catch (IOException ex) { error = ex.Message; }
            Equal(1, calls); Check(error?.Contains("original-copy-error", StringComparison.Ordinal) == true, "元の失敗を上書きした");
            Check(error?.Contains("observer-error", StringComparison.Ordinal) == false, "通知の失敗を出力失敗に混入した");
        })));
        Run("完了通知: 既存完了処理が失敗しても実出力通知は1回行い既存例外を維持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage(); List<CaptureDelivery> received = [];
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask, delivered: received.Add);
            string? error = null;
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory), onDelivered: () => throw new InvalidOperationException("legacy-completion-error")); }
            catch (InvalidOperationException ex) { error = ex.Message; }
            Equal("legacy-completion-error", error); Equal(1, received.Count);
            Equal(OutputDestination.Clipboard, received.Single().Completed); Equal(OutputDestination.None, received.Single().Failed);
        })));
        Run("完了通知: 入力不備では出力前に拒否して完了通知を捏造しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage(); int calls = 0;
            var router = new OutputRouter(Path.Combine(directory, "work"), delivered: _ => calls++);
            bool rejected = false;
            try { await router.ScreenshotAsync(image, (OutputDestination)8, OutputSettings(directory)); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "不正な出力指定を拒否しない");
            rejected = false;
            try { await router.RecordingAsync(Path.Combine(directory, "missing.mp4"), OutputDestination.File, OutputSettings(directory)); }
            catch (FileNotFoundException) { rejected = true; }
            Check(rejected, "欠落した録画ファイルを拒否しない"); Equal(0, calls);
        })));
    }

    private static Task<string> DeliverSample(OutputRouter router, CaptureIntent intent, Bitmap image, string source,
        OutputDestination destination, AppSettings settings) => intent == CaptureIntent.Screenshot
        ? router.ScreenshotAsync(image, destination, settings) : router.RecordingAsync(source, destination, settings);

    private static string DeliverySource(string directory)
    {
        string path = Path.Combine(directory, "synthetic-video-source.mp4");
        File.WriteAllBytes(path, [1, 5, 9, 13]);
        return path;
    }
}
