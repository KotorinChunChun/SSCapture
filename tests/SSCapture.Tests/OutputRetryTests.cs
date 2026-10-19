using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void OutputRetryTests()
    {
        Run("再実行は失敗コピーだけ・成功ファイルと外部送信と履歴を重複させない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int copies = 0, launches = 0, histories = 0, remembered = 0;
            var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 2, ImageEditorPath = CreateFakeEditor(directory) };
            using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), image =>
            {
                Equal(new Size(7, 6), image.Size); Equal(Color.White.ToArgb(), image.GetPixel(0, 0).ToArgb());
                return ++copies == 1 ? Task.FromException(new IOException("copy busy")) : Task.CompletedTask;
            }, launch: _ => launches++, captured: (_, _, _) => histories++);
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, settings,
                onDelivered: () => remembered++));
            Check(router.HasPendingRetry, "失敗画像を保持しない");
            source.Dispose();
            await router.RetryFailedAsync();
            Equal(2, copies); Equal(1, launches); Equal(1, histories); Equal(1, remembered);
            Equal(1, Directory.GetFiles(settings.SaveDirectory).Length); Check(!router.HasPendingRetry, "成功後も再実行が残った");
            Equal<string?>(null, router.RetryNotice);
        })));
        Run("保存失敗の再実行はコピーを繰り返さず外部アプリも完成ファイルを使う", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
            File.WriteAllText(settings.SaveDirectory, "block directory");
            int copies = 0, launches = 0;
            using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copies++; return Task.CompletedTask; }, launch: start =>
            { Check(File.Exists(start.ArgumentList.Single()), "未完成のファイル"); launches++; });
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, settings, fileName: "same.png"));
            Equal(1, copies); Equal(0, launches);
            File.Delete(settings.SaveDirectory);
            await router.RetryFailedAsync();
            Equal(1, copies); Equal(1, launches); Check(File.Exists(Path.Combine(settings.SaveDirectory, "same.png")), "命名が変わった");
        })));
        Run("外部アプリだけ再試行すると保存済みパスを再利用しファイルを増やさない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
            var paths = new List<string>();
            using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), launch: start =>
            { paths.Add(start.ArgumentList.Single()); if (paths.Count == 1) throw new IOException("editor busy"); });
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.File | OutputDestination.ExternalApp, settings));
            await router.RetryFailedAsync();
            Equal(2, paths.Count); Equal(paths[0], paths[1]); Equal(1, Directory.GetFiles(settings.SaveDirectory).Length);
        })));
        Run("ファイル保存OFFの外部送信再実行も作業ファイルと白余白を重複させない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory), ScreenshotPaddingPixels = 10 };
            var paths = new List<string>(); using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), launch: start =>
            {
                paths.Add(start.ArgumentList.Single());
                using var image = new Bitmap(paths[^1]); Equal(new Size(23, 22), image.Size);
                if (paths.Count == 1) throw new IOException("editor busy");
            });
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.ExternalApp, settings));
            await router.RetryFailedAsync(); Equal(paths[0], paths[1]);
            Equal(1, Directory.GetFiles(Path.Combine(directory, "work"), "*.png", SearchOption.AllDirectories).Length);
        })));
        Run("保存後フォルダーだけの失敗は保存せず表示だけやり直す", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int folders = 0; var settings = OutputSettings(directory) with { OpenFolderAfterSave = true };
            using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), launch: _ => { if (++folders == 1) throw new IOException("explorer busy"); });
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.File, settings));
            string message = await router.RetryFailedAsync();
            Equal(2, folders); Equal(1, Directory.GetFiles(settings.SaveDirectory).Length);
            Check(message.Contains("保存フォルダを開きました"), "再表示成功が分からない");
        })));
        Run("命名完了後の保存再実行は同じ名前を使い再度命名しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int names = 0; var settings = OutputSettings(directory);
            File.WriteAllText(settings.SaveDirectory, "block directory"); using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"));
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.File, settings, requestFileName: () =>
            { names++; return Task.FromResult<ScreenshotFileName?>(new("named.png", null, false)); }));
            File.Delete(settings.SaveDirectory); await router.RetryFailedAsync();
            Equal(1, names); Check(File.Exists(Path.Combine(settings.SaveDirectory, "named.png")), "名前が変わった");
        })));
        Run("命名失敗の再実行は保持画像を使い元画像の破棄後も完了", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int names = 0; var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 2 };
            using var source = OutputImage(); using var router = new OutputRouter(Path.Combine(directory, "work"));
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.File, settings, requestFileNameForImage: image =>
            {
                Equal(new Size(7, 6), image.Size);
                return ++names == 1 ? Task.FromException<ScreenshotFileName?>(new IOException("naming failed"))
                    : Task.FromResult<ScreenshotFileName?>(new("recovered.png", null, false));
            }));
            source.Dispose(); await router.RetryFailedAsync();
            Equal(2, names); Check(File.Exists(Path.Combine(settings.SaveDirectory, "recovered.png")), "命名を再実行しない");
        })));
        Run("命名キャンセルは失敗再実行にしない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var source = OutputImage(); using var router = new OutputRouter(Path.Combine(directory, "work"));
            await router.ScreenshotAsync(source, OutputDestination.File | OutputDestination.ExternalApp, OutputSettings(directory),
                requestFileName: () => Task.FromResult<ScreenshotFileName?>(null));
            Check(!router.HasPendingRetry, "キャンセルした出力を再実行にした");
        })));
        Run("再実行画像の保持上限とDisposeで確実に解放", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var source = OutputImage();
            using var small = new OutputRouter(Path.Combine(directory, "work"), _ => Task.FromException(new IOException("busy")), retryByteLimit: 4);
            await RetryFailure(() => small.ScreenshotAsync(source, OutputDestination.Clipboard, OutputSettings(directory)));
            Check(!small.HasPendingRetry && small.RetryNotice!.Contains("保持上限"), "上限が効いていない");
            using var normal = new OutputRouter(Path.Combine(directory, "work"), _ => Task.FromException(new IOException("busy")));
            await RetryFailure(() => normal.ScreenshotAsync(source, OutputDestination.Clipboard, OutputSettings(directory)));
            Check(normal.HasPendingRetry, "保持できない"); normal.Dispose(); Check(!normal.HasPendingRetry, "Dispose後も保持");
            bool rejected = false;
            try { await normal.RetryFailedAsync(); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "破棄後に実行した");
        })));
        Run("再実行中の多重呼び出しを拒否し成功後は消去", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int copies = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = OutputImage();
            using var router = new OutputRouter(Path.Combine(directory, "work"), _ => ++copies == 1 ? Task.FromException(new IOException("busy")) : release.Task);
            await RetryFailure(() => router.ScreenshotAsync(source, OutputDestination.Clipboard, OutputSettings(directory)));
            Task<string> first = router.RetryFailedAsync();
            bool rejected = false;
            try { await router.RetryFailedAsync(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !router.HasPendingRetry, "多重再実行を受理した");
            release.SetResult(); await first; Equal(2, copies); Check(!router.HasPendingRetry, "成功後も残る");
        })));
        Run("遅れて失敗した古い出力で最新の再試行画像を上書きしない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int calls = 0; var old = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var red = new Bitmap(1, 1); red.SetPixel(0, 0, Color.Red);
            using var blue = new Bitmap(1, 1); blue.SetPixel(0, 0, Color.Blue);
            using var router = new OutputRouter(Path.Combine(directory, "work"), image =>
            {
                calls++;
                if (calls == 1) return old.Task;
                if (calls == 2) return Task.FromException(new IOException("new failure"));
                Equal(Color.Blue.ToArgb(), image.GetPixel(0, 0).ToArgb()); return Task.CompletedTask;
            });
            Task<string> first = router.ScreenshotAsync(red, OutputDestination.Clipboard, OutputSettings(directory));
            await RetryFailure(() => router.ScreenshotAsync(blue, OutputDestination.Clipboard, OutputSettings(directory)));
            old.SetException(new IOException("old failure")); await RetryFailure(async () => await first);
            await router.RetryFailedAsync(); Equal(3, calls);
        })));
    }

    private static async Task RetryFailure(Func<Task> action)
    {
        bool failed = false;
        try { await action(); } catch (IOException) { failed = true; }
        Check(failed, "失敗を隠した");
    }
}
