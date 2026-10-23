using System.Diagnostics;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void SaveCancellationTests()
    {
        Run("保存キャンセル: コピー完了後に命名を表示しキャンセル後は再コピーしない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage();
            using var probe = new SaveCancellationProbe(directory);
            var copyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var namingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var namingResult = new TaskCompletionSource<ScreenshotFileName?>(TaskCreationOptions.RunContinuationsAsynchronously);
            probe.CopyWait = copyReady.Task;
            DateTimeOffset capturedAt = SaveCancellationTime;
            var bounds = new Rectangle(-1200, 50, image.Width, image.Height);
            int requests = 0;
            Task<string> operation = probe.Router.ScreenshotAsync(image, (OutputDestination)7,
                OutputSettings(directory) with { OpenFolderAfterSave = true }, capturedAt: capturedAt,
                onDelivered: probe.RememberRegion, captureBounds: bounds, requestFileName: () =>
                {
                    requests++;
                    probe.Order.Add("name");
                    Equal("capture", probe.Clipboard);
                    namingStarted.SetResult();
                    return namingResult.Task;
                });
            Equal(1, probe.Copies); Equal(0, requests);
            copyReady.SetResult();
            await namingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!operation.IsCompleted, "命名待ちを完了扱いにしている");
            Equal(0, probe.CapturedCalls); Equal(0, probe.Notifications.Count); Equal(0, probe.Regions);
            Equal(0, Directory.GetFileSystemEntries(directory).Length);
            // Simulate the user copying unrelated content while the naming window is open.
            probe.Clipboard = "user copied another item";
            namingResult.SetResult(null);
            string result = await operation;
            Equal("user copied another item", probe.Clipboard);
            Equal(1, probe.Copies); Equal(1, requests); Equal(0, probe.Launches.Count);
            Check(result.Contains("コピーしました。", StringComparison.Ordinal) && result.Contains("ファイル保存をキャンセルしました。", StringComparison.Ordinal), "コピーとキャンセルの両結果がない");
            Equal("copy,name,history,region,notice", string.Join(',', probe.Order));
            Equal(1, probe.CapturedCalls); Equal(1, probe.History.Entries.Count);
            Equal(capturedAt, probe.History.Entries[0].CapturedAt);
            Equal<Rectangle?>(bounds, probe.History.Entries[0].CaptureBounds);
            Equal<string?>(null, probe.History.Entries[0].SavedPath);
            Equal(new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, CaptureBounds: bounds), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null });
            Equal(0, Directory.GetFileSystemEntries(directory).Length);
        })));

        foreach (bool openFolder in new[] { false, true })
        {
            Run($"保存確定: 命名結果とフォルダ{openFolder}を反映しコピー・履歴・通知は各1回", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage();
                using var probe = new SaveCancellationProbe(directory);
                string editor = CreateFakeEditor(directory);
                var settings = OutputSettings(directory) with { ImageEditorPath = editor, OpenFolderAfterSave = !openFolder };
                var edited = new FileNamePlan("編集_{yyyymmdd}_{hhmmss}_{##}_{ocr}.png", SaveCancellationTime.AddDays(-2), CaptureIntent.Screenshot, "名前");
                Directory.CreateDirectory(settings.SaveDirectory);
                string first = Path.Combine(settings.SaveDirectory, edited.Candidate(0));
                File.WriteAllBytes(first, [3, 7, 11]);
                string expected = Path.Combine(settings.SaveDirectory, edited.Candidate(1));
                var bounds = new Rectangle(10, 20, image.Width, image.Height);
                probe.OnLaunch = _ =>
                {
                    using var savedImage = new Bitmap(expected);
                    Equal(Color.Magenta.ToArgb(), savedImage.GetPixel(1, 1).ToArgb());
                };
                int requests = 0;
                string result = await probe.Router.ScreenshotAsync(image, (OutputDestination)7, settings, capturedAt: SaveCancellationTime,
                    onDelivered: probe.RememberRegion, captureBounds: bounds, requestFileName: () =>
                    {
                        requests++; probe.Order.Add("name");
                        Equal("capture", probe.Clipboard);
                        probe.Clipboard = "user copied another item";
                        return Task.FromResult<ScreenshotFileName?>(new("ignored-because-plan.png", edited, openFolder));
                    });
                Equal(1, probe.Copies); Equal(1, requests); Equal("user copied another item", probe.Clipboard);
                Equal(openFolder ? 2 : 1, probe.Launches.Count);
                Equal(editor, probe.Launches[0].FileName);
                Equal(expected, probe.Launches[0].ArgumentList.Single());
                if (openFolder) Equal("/select,\"" + expected + "\"", probe.Launches[1].Arguments);
                Equal(1, probe.CapturedCalls); Equal(1, probe.Regions);
                Equal(SaveCancellationTime, probe.History.Entries.Single().CapturedAt);
                Equal(expected, probe.History.Entries.Single().SavedPath);
                Equal<Rectangle?>(bounds, probe.History.Entries.Single().CaptureBounds);
                Equal(new CaptureDelivery(CaptureIntent.Screenshot, (OutputDestination)7, OutputDestination.None, SavedPath: expected,
                    CaptureBounds: bounds, CaptureDescription: "デスクトップ【1】の 【10,20】 ～ 【13,22】"), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null });
                Check(result.Contains(expected, StringComparison.Ordinal), "確定した保存先が結果にない");
                Check(File.ReadAllBytes(first).SequenceEqual(new byte[] { 3, 7, 11 }), "既存ファイルを上書きしている");
                Check(!Directory.Exists(Path.Combine(directory, "work")), "外部アプリ用に別画像を作っている");
            })));
        }

        foreach (OutputDestination destination in new[] { OutputDestination.File, OutputDestination.File | OutputDestination.ExternalApp })
        {
            Run($"保存キャンセル: コピーOFF/{destination}は履歴・範囲更新・通知・ファイルを作らない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage();
                using var probe = new SaveCancellationProbe(directory);
                int requests = 0;
                string result = await probe.Router.ScreenshotAsync(image, destination,
                    OutputSettings(directory) with { OpenFolderAfterSave = true }, onDelivered: probe.RememberRegion, requestFileName: () =>
                    {
                        requests++;
                        return Task.FromResult<ScreenshotFileName?>(null);
                    });
                Equal("ファイル保存をキャンセルしました。", result);
                Equal(1, requests); Equal(0, probe.Copies); Equal("initial", probe.Clipboard);
                Equal(0, probe.CapturedCalls); Equal(0, probe.History.Entries.Count); Equal(0, probe.Regions);
                Equal(0, probe.Notifications.Count); Equal(0, probe.Launches.Count);
                Equal(0, Directory.GetFileSystemEntries(directory).Length);
            })));
        }

        Run("保存キャンセル: コピー失敗は画像回収用履歴とコピー失敗通知を保持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage();
            using var probe = new SaveCancellationProbe(directory) { CopyFailure = new IOException("synthetic clipboard failure") };
            int requests = 0;
            IOException error = await ExpectSaveCancellationErrorAsync(() => probe.Router.ScreenshotAsync(image, (OutputDestination)7,
                OutputSettings(directory), capturedAt: SaveCancellationTime, onDelivered: probe.RememberRegion, requestFileName: () =>
                {
                    requests++;
                    return Task.FromResult<ScreenshotFileName?>(null);
                }));
            Equal(1, requests); Equal(1, probe.Copies); Equal(0, probe.Regions); Equal(0, probe.Launches.Count);
            Check(error.Message.Contains("synthetic clipboard failure", StringComparison.Ordinal) && error.Message.Contains("ファイル保存をキャンセルしました。", StringComparison.Ordinal), "コピー失敗またはキャンセルを隠している");
            Equal(1, probe.CapturedCalls); Equal(SaveCancellationTime, probe.History.Entries.Single().CapturedAt);
            using var recovered = probe.History.CloneImage(probe.History.Entries[0].Id);
            Equal(Color.Magenta.ToArgb(), recovered!.GetPixel(1, 1).ToArgb());
            Equal(new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.None, OutputDestination.Clipboard), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null });
            Equal(0, Directory.GetFileSystemEntries(directory).Length);
        })));

        Run("保存確定: コピー失敗でも命名・保存・外部アプリへ進む", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage();
            using var probe = new SaveCancellationProbe(directory) { CopyFailure = new IOException("synthetic clipboard failure") };
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
            string expected = Path.Combine(settings.SaveDirectory, "コピー失敗後も保存.png");
            int requests = 0;
            IOException error = await ExpectSaveCancellationErrorAsync(() => probe.Router.ScreenshotAsync(image, (OutputDestination)7,
                settings, capturedAt: SaveCancellationTime, onDelivered: probe.RememberRegion, requestFileName: () =>
                {
                    requests++;
                    return Task.FromResult<ScreenshotFileName?>(new("コピー失敗後も保存.png", null, false));
                }));
            Equal(1, requests); Equal(1, probe.Copies); Equal(1, probe.Regions); Equal(1, probe.Launches.Count);
            Check(File.Exists(expected) && error.Message.Contains("synthetic clipboard failure", StringComparison.Ordinal), "保存せずコピー失敗だけで終了している");
            Equal(expected, probe.Launches[0].ArgumentList.Single());
            Equal(expected, probe.History.Entries.Single().SavedPath);
            Equal(SaveCancellationTime, probe.History.Entries.Single().CapturedAt);
            Equal(new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File | OutputDestination.ExternalApp, OutputDestination.Clipboard, SavedPath: expected), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null });
        })));

        foreach (bool clipboard in new[] { false, true })
        {
            Run($"保存命名例外: コピー{clipboard}の実結果を保持しFileだけ失敗にする", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage();
                using var probe = new SaveCancellationProbe(directory);
                OutputDestination destination = OutputDestination.File | OutputDestination.ExternalApp | (clipboard ? OutputDestination.Clipboard : OutputDestination.None);
                var original = new InvalidOperationException("synthetic naming failure");
                int requests = 0;
                IOException error = await ExpectSaveCancellationErrorAsync(() => probe.Router.ScreenshotAsync(image, destination,
                    OutputSettings(directory) with { OpenFolderAfterSave = true }, capturedAt: SaveCancellationTime,
                    onDelivered: probe.RememberRegion, requestFileName: () =>
                    {
                        requests++;
                        if (clipboard) throw original;
                        return Task.FromException<ScreenshotFileName?>(original);
                    }));
                Equal(1, requests); Equal(clipboard ? 1 : 0, probe.Copies); Equal(0, probe.Launches.Count);
                Equal(clipboard ? "capture" : "initial", probe.Clipboard);
                Equal(1, probe.CapturedCalls); Equal(clipboard ? 1 : 0, probe.Regions);
                Equal<string?>(null, probe.History.Entries.Single().SavedPath);
                Equal(SaveCancellationTime, probe.History.Entries.Single().CapturedAt);
                Check(((AggregateException)error.InnerException!).InnerExceptions.Contains(original), "命名処理の元例外が失われた");
                Equal(new CaptureDelivery(CaptureIntent.Screenshot, clipboard ? OutputDestination.Clipboard : OutputDestination.None, OutputDestination.File), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null });
                Equal(0, Directory.GetFileSystemEntries(directory).Length);
            })));
        }

        Run("保存確定後の保存失敗でもコピーを維持し別の作業画像は作らない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage();
            using var probe = new SaveCancellationProbe(directory);
            string blocked = Path.Combine(directory, "save-directory-is-file");
            File.WriteAllText(blocked, "preserve existing file");
            var settings = OutputSettings(directory) with { SaveDirectory = blocked, ImageEditorPath = CreateFakeEditor(directory) };
            await ExpectSaveCancellationErrorAsync(() => probe.Router.ScreenshotAsync(image, (OutputDestination)7, settings,
                capturedAt: SaveCancellationTime, onDelivered: probe.RememberRegion,
                requestFileName: () => Task.FromResult<ScreenshotFileName?>(new("保存.png", null, true))));
            Equal(1, probe.Copies); Equal("capture", probe.Clipboard); Equal(0, probe.Launches.Count);
            Equal(1, probe.CapturedCalls); Equal(1, probe.Regions);
            Equal<string?>(null, probe.History.Entries.Single().SavedPath);
            Equal(SaveCancellationTime, probe.History.Entries.Single().CapturedAt);
            Equal(new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.File | OutputDestination.ExternalApp), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null });
            Equal("preserve existing file", File.ReadAllText(blocked));
            Check(!Directory.Exists(Path.Combine(directory, "work")), "保存失敗後に外部アプリ用画像を作った");
        })));

        foreach (OutputDestination destination in new[] { OutputDestination.None, OutputDestination.Clipboard, OutputDestination.ExternalApp, OutputDestination.Clipboard | OutputDestination.ExternalApp })
        {
            Run($"保存OFF/{destination}では命名コールバックを呼ばず既存の出力を維持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var image = OutputImage();
                using var probe = new SaveCancellationProbe(directory);
                var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
                int requests = 0;
                await probe.Router.ScreenshotAsync(image, destination, settings, capturedAt: SaveCancellationTime,
                    onDelivered: probe.RememberRegion, requestFileName: () =>
                    {
                        requests++;
                        throw new InvalidOperationException("File OFF must not request naming");
                    });
                Equal(0, requests);
                Equal(destination.HasFlag(OutputDestination.Clipboard) ? 1 : 0, probe.Copies);
                Equal(destination.HasFlag(OutputDestination.ExternalApp) ? 1 : 0, probe.Launches.Count);
                Equal(destination == OutputDestination.None ? 0 : 1, probe.CapturedCalls);
                Equal(destination == OutputDestination.None ? 0 : 1, probe.Notifications.Count);
                if (destination != OutputDestination.None)
                    Equal(new CaptureDelivery(CaptureIntent.Screenshot, destination, OutputDestination.None), probe.Notifications.Single() with { CaptureSettings = null, NotificationImage = null, NotificationPath = null });
                Check(!Directory.Exists(settings.SaveDirectory), "保存OFFで保存先を生成している");
            })));
        }
    }

    private static DateTimeOffset SaveCancellationTime => new(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));

    private static async Task<IOException> ExpectSaveCancellationErrorAsync(Func<Task<string>> action)
    {
        try { await action(); }
        catch (IOException error) { return error; }
        throw new InvalidOperationException("出力失敗が通知されていません。");
    }

    private sealed class SaveCancellationProbe : IDisposable
    {
        internal OutputRouter Router { get; }
        internal CaptureHistory History { get; } = new();
        internal List<CaptureDelivery> Notifications { get; } = [];
        internal List<ProcessStartInfo> Launches { get; } = [];
        internal List<string> Order { get; } = [];
        internal Task? CopyWait;
        internal Exception? CopyFailure;
        internal Action<ProcessStartInfo>? OnLaunch;
        internal int Copies, CapturedCalls, Regions;
        internal string Clipboard = "initial";

        internal SaveCancellationProbe(string directory)
        {
            Router = new OutputRouter(Path.Combine(directory, "work"), async image =>
            {
                Copies++; Order.Add("copy");
                if (CopyWait is { } waiting) await waiting;
                if (CopyFailure is { } failure) throw failure;
                Equal(Color.Magenta.ToArgb(), image.GetPixel(1, 1).ToArgb());
                Clipboard = "capture";
            }, launch: info =>
            {
                Launches.Add(info);
                OnLaunch?.Invoke(info);
            }, capturedWithBounds: (image, path, time, bounds) =>
            {
                CapturedCalls++; Order.Add("history");
                History.Add(image, path, time, bounds);
            }, delivered: result =>
            {
                Order.Add("notice"); Notifications.Add(result);
            }, readDisplays: () => [new SSCapture.Capture.DisplayTarget("fixture", new(0, 0, 1920, 1080), true)]);
        }

        internal void RememberRegion() { Regions++; Order.Add("region"); }
        public void Dispose() => History.Dispose();
    }
}
