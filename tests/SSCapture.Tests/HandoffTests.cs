using System.Diagnostics;
using System.Text.Json;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void HandoffTests()
    {
        Run("未保存の通知は撮影時の範囲名と日時を保持し、OCR・完成PNGを必要時だけ生成", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                CaptureDelivery? delivery = null;
                int calls = 0;
                var time = new DateTimeOffset(2026, 11, 22, 12, 34, 56, TimeSpan.FromHours(9));
                var plan = new FileNamePlan("{yyyymmdd}_{hhmmss}_{region}_{ocr}_{##}.{ext}", time, CaptureIntent.Screenshot, Region: "入力 A");
                using var image = OutputImage();
                using var router = new OutputRouter(directory, _ => Task.CompletedTask, delivered: value => delivery = value,
                    recognizeForHandoff: (_, _, _) =>
                    {
                        Interlocked.Increment(ref calls);
                        return Task.FromResult(new OcrResult(["読 み"]));
                    });
                await router.ScreenshotAsync(image, OutputDestination.Clipboard,
                    OutputSettings(directory) with { FileNamePattern = plan.Pattern, ScreenshotColorDepth = ScreenshotColorDepth.Indexed4 }, namePlan: plan);
                Equal(0, calls);
                Check(!Directory.EnumerateFileSystemEntries(directory).Any(), "クリップボード出力で作業ファイルを作った");
                using var pending = delivery!.NotificationImage!;
                var paths = await Task.WhenAll(pending.PrepareAsync(), pending.PrepareAsync());
                Equal(1, calls);
                var names = paths.Select(Path.GetFileName).Order().ToArray();
                Equal("20261122_123456_入力 A_読み_01.png", names[0]);
                Equal("20261122_123456_入力 A_読み_02.png", names[1]);
                foreach (string path in paths)
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    Equal((byte)4, bytes[24]); Equal((byte)3, bytes[25]);
                }
            })));

        Run("PNG関連付けの再試行はアプリを再解決し完成済みの命名済み画像を渡す", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var app = new ExternalApplication { Id = "snip", Name = "Snipping Tool", Launch = ExternalApplicationLaunch.SnippingToolPng };
                var settings = OutputSettings(directory) with { ImageEditors = [app], SelectedImageEditorId = app.Id };
                var plan = new FileNamePlan("{ocr}_{region}.{ext}", DateTimeOffset.Now, CaptureIntent.Screenshot, Ocr: "確定", Region: "窓");
                string? association = null;
                List<string> paths = [];
                int attempts = 0, copies = 0;
                var editor = new ExternalEditor(findAssociation: kind => { Equal(app.Launch, kind); return association; },
                    openAssociated: (kind, path) =>
                    {
                        Equal("selected-png", kind);
                        using var opened = new Bitmap(path);
                        Equal(new Size(3, 2), opened.Size);
                        paths.Add(path);
                        if (++attempts == 1) throw new IOException("編集アプリが使用中");
                    });
                using var router = new OutputRouter(directory, _ => { copies++; return Task.CompletedTask; }, externalEditor: editor,
                    recognizeForHandoff: (_, _, _) => throw new InvalidOperationException("確定文字列を再認識した"));
                using var image = OutputImage();
                await RetryFailure(() => router.ScreenshotAsync(image, OutputDestination.Clipboard | OutputDestination.ExternalApp, settings, namePlan: plan));
                Equal(1, copies);
                Check(!Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories).Any(), "未登録アプリ用に画像を作った");
                association = "selected-png";
                await RetryFailure(() => router.RetryFailedAsync());
                await router.RetryFailedAsync();
                Equal(1, copies); Equal(2, paths.Count); Equal(paths[0], paths[1]);
                Equal("確定_窓.png", Path.GetFileName(paths[0]));
            })));

        Run("録画を直接編集すると完成MP4を撮影時の命名規則で受け渡す", () =>
            WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                string source = Path.Combine(directory, "raw.mp4");
                File.WriteAllBytes(source, [1, 2, 3]);
                var plan = new FileNamePlan("{region}_{hhmmss}.{ext}", new(2026, 11, 22, 20, 21, 22, TimeSpan.Zero),
                    CaptureIntent.Recording, Region: "録画");
                string? path = null;
                using var router = new OutputRouter(directory, launch: info => path = info.ArgumentList.Single());
                await router.RecordingAsync(source, OutputDestination.ExternalApp,
                    OutputSettings(directory) with { VideoEditorPath = CreateFakeEditor(directory) }, namePlan: plan);
                Equal("録画_202122.mp4", Path.GetFileName(path));
                Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(path!)), "完成動画と一致しない");
            })));

        Run("別プロセスの同名生成は衝突せず起動時回収は使用中・移動済み・置換済みを保護", () =>
            WithOutputDirectory(directory =>
            {
                using var first = StartHandoffWorker(directory);
                using var second = StartHandoffWorker(directory);
                using var third = StartHandoffWorker(directory);
                try
                {
                    string[] paths = [ReadHandoffWorker(first), ReadHandoffWorker(second), ReadHandoffWorker(third)];
                    Equal("同名.png,同名_0001.png,同名_0002.png", string.Join(',', paths.Select(Path.GetFileName).Order(StringComparer.Ordinal)));
                    var expired = DateTimeOffset.UtcNow.AddMinutes(2);
                    TemporaryHandoff.CleanupPreviousSession(directory, expired);
                    Check(paths.All(File.Exists), "別プロセスが使用中のファイルを回収した");

                    string moved = Path.Combine(directory, "移動先.png");
                    File.Move(paths[1], moved);
                    File.Delete(paths[2]);
                    File.WriteAllText(paths[2], "別の所有者");
                    foreach (Process worker in new[] { first, second, third })
                    {
                        worker.StandardInput.WriteLine();
                        Check(worker.WaitForExit(10_000) && worker.ExitCode == 0, "生成プロセスが停止しない");
                    }
                    TemporaryHandoff.CleanupPreviousSession(directory, expired);
                    Check(!File.Exists(paths[0]) && File.Exists(moved), "回収対象またはドロップ先の扱いが不正");
                    Equal("別の所有者", File.ReadAllText(paths[2]));
                    Check(!Directory.EnumerateFiles(Path.Combine(directory, ".handoff-leases")).Any(), "終了した所有記録が残った");
                }
                finally
                {
                    foreach (Process worker in new[] { first, second, third })
                    {
                        if (!worker.HasExited) worker.StandardInput.Close();
                        worker.WaitForExit(10_000);
                    }
                }
            }));

        Run("PNG編集アプリはSnipping Toolのパッケージを識別する", () =>
        {
            Check(PngEditorAssociation.Matches(PngEditorAssociation.SnippingToolAppId), "Snipping Toolを識別できない");
            Check(!PngEditorAssociation.Matches("Other!App"), "別のPNGハンドラーを選んだ");
            Equal(1, EditorPresets.Images.Count(preset => preset.Launch != ExternalApplicationLaunch.Executable));
            var old = JsonSerializer.Deserialize<ExternalApplication>("{\"Id\":\"legacy\",\"Name\":\"旧登録\"}")!;
            Equal(ExternalApplicationLaunch.Executable, old.Launch);
        });
    }

    private static int HoldTestHandoff(string directory)
    {
        using var image = OutputImage();
        using var handoff = TemporaryHandoff.Publish(directory, publish =>
            CaptureStorage.SavePng(image, directory, DateTimeOffset.Now, "同名.png", beforePublish: publish));
        Console.WriteLine(handoff.Path);
        Console.ReadLine();
        return 0;
    }

    private static Process StartHandoffWorker(string directory)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--handoff-test-worker");
        start.ArgumentList.Add(directory);
        return Process.Start(start) ?? throw new IOException("受け渡し検証プロセスを開始できません。");
    }

    private static string ReadHandoffWorker(Process worker) =>
        worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()
            ?? throw new IOException("受け渡し検証プロセスがパスを返しませんでした。");
}
