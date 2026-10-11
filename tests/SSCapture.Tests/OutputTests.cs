using System.Collections.Concurrent;
using System.Diagnostics;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void OutputTests()
    {
        Run("履歴は取得時刻を保持し命名時刻や完了順に左右されない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var history = new CaptureHistory(); using var image = OutputImage();
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask,
                captured: (bitmap, path, time) => history.Add(bitmap, path, time));
            var first = DateTimeOffset.Now; var second = first.AddMinutes(1);
            var editedName = new FileNamePlan("{yyyymmdd}.png", first.AddDays(-10), CaptureIntent.Screenshot);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory), namePlan: editedName, capturedAt: second);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory), capturedAt: first);
            Equal(second, history.Entries[0].CapturedAt); Equal(first, history.Entries[1].CapturedAt);
        })));
        Run("一部出力成功は範囲更新を通知し全失敗なら通知しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage(); int delivered = 0;
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.FromException(new IOException("clipboard busy")));
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard | OutputDestination.File, OutputSettings(directory), onDelivered: () => delivered++); }
            catch (IOException) { }
            Equal(1, delivered);
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory), onDelivered: () => delivered++); }
            catch (IOException) { }
            Equal(1, delivered);
        })));
        Run("履歴連携は保存OFF画像をディスクへ書かず再コピー可能", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var history = new CaptureHistory();
            using var image = OutputImage();
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.CompletedTask,
                capturedWithBounds: (bitmap, path, time, bounds) => history.Add(bitmap, path, time, bounds));
            var bounds = new Rectangle(-1920, 10, image.Width, image.Height);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory), captureBounds: bounds);
            Equal(1, history.Entries.Count); Equal<string?>(null, history.Entries.Single().SavedPath);
            Equal<Rectangle?>(bounds, history.Entries.Single().CaptureBounds);
            using var copy = history.CloneImage(history.Entries.Single().Id);
            Equal(Color.Magenta.ToArgb(), copy!.GetPixel(1, 1).ToArgb());
            Equal(0, Directory.GetFileSystemEntries(directory).Length);
            await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory) with { KeepHistory = false });
            await router.ScreenshotAsync(image, OutputDestination.None, OutputSettings(directory));
            Equal(1, history.Entries.Count);
        })));
        Run("保存済み履歴は実際の衝突回避パスを保持し消去でもファイルを残す", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var history = new CaptureHistory(); using var image = OutputImage();
            var settings = OutputSettings(directory);
            var router = new OutputRouter(Path.Combine(directory, "work"), captured: (bitmap, path, time) => history.Add(bitmap, path, time));
            var plan = new FileNamePlan("{region}_{##}.png", DateTimeOffset.Now, CaptureIntent.Screenshot, Region: "範囲");
            await router.ScreenshotAsync(image, OutputDestination.File, settings, namePlan: plan);
            await router.ScreenshotAsync(image, OutputDestination.File, settings, namePlan: plan);
            Equal("範囲_02.png", Path.GetFileName(history.Entries[0].SavedPath));
            Check(history.Entries.All(e => File.Exists(e.SavedPath)), "履歴に保存パスがない");
            history.Clear(); Equal(2, Directory.GetFiles(settings.SaveDirectory).Length);
        })));
        Run("出力失敗でも画像履歴を保持し後で再コピー可能", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var history = new CaptureHistory(); using var image = OutputImage();
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => Task.FromException(new IOException("clipboard busy")),
                captured: (bitmap, path, time) => history.Add(bitmap, path, time));
            bool failed = false;
            try { await router.ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory)); }
            catch (IOException) { failed = true; }
            Check(failed, "出力の失敗を隠す"); Equal(1, history.Entries.Count);
            Equal(0, Directory.GetFileSystemEntries(directory).Length);
        })));
        Run("Explorer選択は空白・日本語・カンマを含む実ファイルを直接指定する", () => WithOutputDirectory(directory =>
        {
            string path = Path.Combine(directory, "撮影 ファイル,01.png");
            File.WriteAllBytes(path, [1, 2, 3]);
            ProcessStartInfo info = ExplorerSelection.StartInfo(path);
            Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), info.FileName);
            Equal("/select,\"" + path + "\"", info.Arguments);
            Equal(0, info.ArgumentList.Count);
            Check(!info.UseShellExecute, "シェル文字列として実行しない");
        }));
        Run("Explorer選択は相対パス・欠落ファイル・フォルダを起動前に拒否する", () => WithOutputDirectory(directory =>
        {
            int starts = 0;
            foreach (string path in new[] { "relative.png", Path.Combine(directory, "missing.png"), directory })
            {
                bool rejected = false;
                try { ExplorerSelection.Open(path, _ => starts++); }
                catch (ArgumentException) { rejected = true; }
                catch (FileNotFoundException) { rejected = true; }
                Check(rejected, "保存された実ファイル以外を受け付けた");
            }
            Equal(0, starts);
        }));
        Run("Explorer選択は確定パスを1回だけ選択処理へ渡して例外を保持する", () => WithOutputDirectory(directory =>
        {
            string path = Path.Combine(directory, "撮影 ファイル,01.png"); File.WriteAllBytes(path, [1]);
            List<string> selected = [];
            ExplorerSelection.Open(Path.Combine(directory, ".", Path.GetFileName(path)), selected.Add);
            Equal(path, selected.Single());
            var failure = new IOException("synthetic Shell selection failure");
            Exception? observed = null;
            int calls = 0;
            try { ExplorerSelection.Open(path, _ => { calls++; throw failure; }); }
            catch (IOException ex) { observed = ex; }
            Equal(1, calls);
            Check(ReferenceEquals(failure, observed), "選択APIの例外を変更した");
        }));
        foreach (CaptureIntent intent in Enum.GetValues<CaptureIntent>())
        {
            Run($"保存後選択: {intent}は衝突回避後の完成ファイルだけを開く", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var settings = OutputSettings(directory) with { SaveDirectory = Path.Combine(directory, "保存 folder,001"), OpenFolderAfterSave = true };
                Directory.CreateDirectory(settings.SaveDirectory);
                var timestamp = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));
                var plan = new FileNamePlan("{yyyymmdd}_{hhmmss}_{##}.png", timestamp, intent);
                string first = Path.Combine(settings.SaveDirectory, plan.Candidate(0));
                string selected = Path.Combine(settings.SaveDirectory, plan.Candidate(1));
                File.WriteAllBytes(first, [17, 23]);
                using var image = OutputImage(); string source = DeliverySource(directory);
                int launches = 0;
                var router = new OutputRouter(Path.Combine(directory, "work"), launch: info =>
                {
                    launches++;
                    Equal("/select,\"" + selected + "\"", info.Arguments);
                    Check(!info.UseShellExecute, "保存ファイルの選択にシェル実行は不要");
                    using var stream = new FileStream(selected, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (intent == CaptureIntent.Screenshot)
                    {
                        using var png = new Bitmap(stream);
                        Equal(image.Size, png.Size); Equal(Color.Magenta.ToArgb(), png.GetPixel(1, 1).ToArgb());
                    }
                    else
                    {
                        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                        Check(File.ReadAllBytes(source).SequenceEqual(bytes.ToArray()), "動画の全バイトを起動前に公開する");
                    }
                    Equal(0, Directory.GetFiles(settings.SaveDirectory, "*.tmp").Length);
                });
                if (intent == CaptureIntent.Screenshot)
                    await router.ScreenshotAsync(image, OutputDestination.File, settings, namePlan: plan);
                else await router.RecordingAsync(source, OutputDestination.File, settings, namePlan: plan);
                Equal(1, launches);
                Check(new byte[] { 17, 23 }.SequenceEqual(File.ReadAllBytes(first)), "同名の既存ファイルを保持する");
            })));
            Run($"保存後選択: {intent}の成功時のみ・OFFと保存失敗では開かない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var settings = OutputSettings(directory) with { OpenFolderAfterSave = true };
                var probe = new OutputProbe(); using var image = OutputImage(); string source = DeliverySource(directory);
                await DeliverSample(probe.Router(directory), intent, image, source, OutputDestination.File, settings);
                string saved = Directory.GetFiles(settings.SaveDirectory).Single();
                Equal("/select,\"" + saved + "\"", probe.Launches.Single().Arguments);
                Check(!probe.Launches.Single().UseShellExecute, "Explorerを直接起動する");
                await DeliverSample(probe.Router(directory), intent, image, source, OutputDestination.Clipboard, settings);
                await DeliverSample(probe.Router(directory), intent, image, source, OutputDestination.File, settings with { OpenFolderAfterSave = false });
                Equal(1, probe.Launches.Count);
                string blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "keep");
                try { await DeliverSample(probe.Router(directory), intent, image, source, OutputDestination.File, settings with { SaveDirectory = blocked }); }
                catch (IOException) { }
                Equal(1, probe.Launches.Count);
            })));
        }
        Run("フォルダ表示失敗でも保存済みファイルを保持して区別する", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory) with { OpenFolderAfterSave = true };
            var probe = new OutputProbe { FailLaunch = true }; using var image = OutputImage();
            string error = "";
            try { await probe.Router(directory).ScreenshotAsync(image, OutputDestination.File, settings); }
            catch (IOException ex) { error = ex.Message; }
            Equal(1, Directory.GetFiles(settings.SaveDirectory, "*.png").Length);
            Check(error.Contains("保存済みですがフォルダを開けません", StringComparison.Ordinal), "保存失敗と区別する");
        })));
        Run("確定したOCR名を画像・動画保存と外部アプリで共有", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory) with { FileNamePattern = "{ocr}.png", ImageEditorPath = CreateFakeEditor(directory) };
            settings = settings with { VideoEditorPath = settings.ImageEditorPath };
            var probe = new OutputProbe();
            using var image = OutputImage();
            await probe.Router(directory).ScreenshotAsync(image, OutputDestination.File | OutputDestination.ExternalApp, settings, "東京_123456.png");
            Equal(Path.Combine(settings.SaveDirectory, "東京_123456.png"), probe.Launches.Single().ArgumentList.Single());
            string video = Path.Combine(directory, "video.mp4"); File.WriteAllBytes(video, [1, 2, 3]);
            await probe.Router(directory).RecordingAsync(video, OutputDestination.File | OutputDestination.ExternalApp, settings, "東京_123456.mp4");
            Equal(Path.Combine(settings.SaveDirectory, "東京_123456.mp4"), probe.Launches[1].ArgumentList.Single());
            Check(!Directory.Exists(Path.Combine(directory, "work")), "命名保存時に作業画像を重複しない");
        })));
        Run("OCRパターンでも保存OFFなら画像出力にOCR文字列不要", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = OutputSettings(directory) with { FileNamePattern = "{ocr}.png", ImageEditorPath = CreateFakeEditor(directory) };
            using var image = OutputImage(); var probe = new OutputProbe();
            await probe.Router(directory).ScreenshotAsync(image, OutputDestination.Clipboard | OutputDestination.ExternalApp, settings);
            Equal(1, probe.ImageCopies); Equal(1, probe.Launches.Count);
            Check(!Directory.Exists(settings.SaveDirectory), "保存OFFで命名保存しない");
        })));
        for (int mask = 0; mask < 8; mask++)
        {
            OutputDestination selected = (OutputDestination)mask;
            Run($"画像・動画の出力組合せ {selected} と保存済みパス共有", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                string editor = CreateFakeEditor(directory);
                var settings = OutputSettings(directory) with { ImageEditorPath = editor, VideoEditorPath = editor,
                    ImageEditorArguments = "--open \"{file}\" --name \"{filename}\"", VideoEditorArguments = "\"{file}\"" };
                var probe = new OutputProbe();
                using var image = OutputImage();
                await probe.Router(directory).ScreenshotAsync(image, selected, settings);
                Equal(selected.HasFlag(OutputDestination.Clipboard) ? 1 : 0, probe.ImageCopies);
                Equal(selected.HasFlag(OutputDestination.ExternalApp) ? 1 : 0, probe.Launches.Count);
                Equal(selected.HasFlag(OutputDestination.File), Directory.Exists(settings.SaveDirectory));
                if (probe.Launches.Count > 0)
                {
                    var args = probe.Launches[0].ArgumentList;
                    Equal("--open", args[0]);
                    Equal(Path.GetFileName(args[1]), args[3]);
                    if (selected.HasFlag(OutputDestination.File))
                    {
                        Equal(Directory.GetFiles(settings.SaveDirectory).Single(), args[1]);
                        Check(!Directory.Exists(Path.Combine(directory, "work")), "保存と外部アプリでPNGを重複作成しない");
                    }
                }
                var videoProbe = new OutputProbe();
                string source = Path.Combine(directory, "source.mp4");
                File.WriteAllBytes(source, [1, 2, 3]);
                await videoProbe.Router(directory).RecordingAsync(source, selected, settings);
                Equal(selected.HasFlag(OutputDestination.Clipboard) ? 1 : 0, videoProbe.Files.Count);
                Equal(selected.HasFlag(OutputDestination.ExternalApp) ? 1 : 0, videoProbe.Launches.Count);
                if (selected.HasFlag(OutputDestination.File) && selected.HasFlag(OutputDestination.ExternalApp))
                    Equal(Directory.GetFiles(settings.SaveDirectory, "*.mp4").Single(), videoProbe.Launches[0].ArgumentList.Single());
            })));
        }
        Run("コピー失敗後も保存と外部アプリを実行し成功先を通知", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe { FailImageCopy = true };
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
            using var image = OutputImage();
            string message = "";
            try { await probe.Router(directory).ScreenshotAsync(image, (OutputDestination)7, settings); }
            catch (IOException ex) { message = ex.Message; }
            string saved = Directory.GetFiles(settings.SaveDirectory).Single();
            Equal(saved, probe.Launches.Single().ArgumentList.Single());
            Check(message.Contains(saved, StringComparison.Ordinal) && message.Contains("コピー失敗", StringComparison.Ordinal), "部分成功と失敗を通知する");
        })));
        Run("保存失敗時もコピーを保持し別ファイルで編集を開始しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe();
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
            File.WriteAllText(settings.SaveDirectory, "blocking file");
            using var image = OutputImage();
            bool failed = false;
            try { await probe.Router(directory).ScreenshotAsync(image, (OutputDestination)7, settings); }
            catch (IOException) { failed = true; }
            Check(failed, "保存失敗を通知する");
            Equal(1, probe.ImageCopies);
            Equal(0, probe.Launches.Count);
            Check(!Directory.Exists(Path.Combine(directory, "work")), "失敗を隠す別ファイルは作らない");
        })));
        Run("画像clipboard指定はコピーだけ・自動保存/起動なし", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe();
            var settings = OutputSettings(directory);
            using Bitmap image = OutputImage();
            await probe.Router(directory).ScreenshotAsync(image, OutputDestination.Clipboard, settings);
            Equal(1, probe.ImageCopies);
            Equal(Color.Magenta.ToArgb(), probe.ImagePixel);
            Equal(0, probe.Files.Count);
            Equal(0, probe.Launches.Count);
            Check(!Directory.Exists(settings.SaveDirectory), "コピー時に保存フォルダを作成しない");
            Check(!Directory.Exists(Path.Combine(directory, "work")), "コピー時に作業画像を作成しない");
            Equal(Color.Magenta.ToArgb(), image.GetPixel(1, 1).ToArgb());
        })));
        Run("画像file指定はPNG1件だけ・コピー/起動なし", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe();
            var settings = OutputSettings(directory);
            using Bitmap image = OutputImage();
            await probe.Router(directory).ScreenshotAsync(image, OutputDestination.File, settings);
            string[] files = Directory.GetFiles(settings.SaveDirectory);
            Equal(1, files.Length);
            Equal(".png", Path.GetExtension(files[0]));
            using var saved = new Bitmap(files[0]);
            Equal(Color.Magenta.ToArgb(), saved.GetPixel(1, 1).ToArgb());
            Equal(0, probe.ImageCopies);
            Equal(0, probe.Files.Count);
            Equal(0, probe.Launches.Count);
            Check(!Directory.Exists(Path.Combine(directory, "work")), "file指定で外部アプリ作業画像を作成しない");
        })));
        Run("画像external指定は作業PNGだけ・設定保存先/コピーなし", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe();
            string editor = CreateFakeEditor(directory);
            var settings = OutputSettings(directory) with { ImageEditorPath = editor };
            using Bitmap image = OutputImage();
            await probe.Router(directory).ScreenshotAsync(image, OutputDestination.ExternalApp, settings);
            string[] files = Directory.GetFiles(Path.Combine(directory, "work"), "*.png", SearchOption.AllDirectories);
            Equal(1, files.Length);
            using var saved = new Bitmap(files[0]);
            Equal(Color.Magenta.ToArgb(), saved.GetPixel(1, 1).ToArgb());
            Equal(1, probe.Launches.Count);
            Equal(editor, probe.Launches[0].FileName);
            Equal(files[0], probe.Launches[0].ArgumentList.Single());
            Equal(0, probe.ImageCopies);
            Check(!Directory.Exists(settings.SaveDirectory), "external指定で通常保存しない");
        })));
        Run("コピー失敗時に未選択のfile/externalへ流さない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe { FailImageCopy = true };
            using Bitmap image = OutputImage();
            bool rejected = false;
            try { await probe.Router(directory).ScreenshotAsync(image, OutputDestination.Clipboard, OutputSettings(directory)); }
            catch (IOException) { rejected = true; }
            Check(rejected, "コピー失敗を通知する");
            Equal(1, probe.ImageCopies);
            Equal(0, probe.Launches.Count);
            Equal(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
            Equal(Color.Magenta.ToArgb(), image.GetPixel(1, 1).ToArgb());
        })));
        Run("外部アプリ指定不正はPNG作成前に拒否", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe();
            using Bitmap image = OutputImage();
            bool rejected = false;
            try { await probe.Router(directory).ScreenshotAsync(image, OutputDestination.ExternalApp, OutputSettings(directory)); }
            catch (IOException) { rejected = true; }
            Check(rejected, "未設定の外部アプリを拒否する");
            Equal(0, probe.Launches.Count);
            Equal(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        })));
        Run("外部アプリ起動失敗でも作業PNGを保持し場所を通知", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe { FailLaunch = true };
            var settings = OutputSettings(directory) with { ImageEditorPath = CreateFakeEditor(directory) };
            using Bitmap image = OutputImage();
            string? failure = null;
            try { await probe.Router(directory).ScreenshotAsync(image, OutputDestination.ExternalApp, settings); }
            catch (IOException ex) { failure = ex.Message; }
            string saved = Directory.GetFiles(Path.Combine(directory, "work"), "*.png", SearchOption.AllDirectories).Single();
            Check(failure is not null && failure.Contains(saved, StringComparison.Ordinal), "回収可能な画像パスを例外で通知する");
            using var read = new Bitmap(saved);
            Equal(Color.Magenta.ToArgb(), read.GetPixel(1, 1).ToArgb());
            Check(!Directory.Exists(settings.SaveDirectory), "起動失敗でも未選択の保存先へ出力しない");
        })));

        foreach (OutputDestination destination in Enum.GetValues<OutputDestination>())
        {
            Run($"動画{destination}指定だけに出力・原本を保持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                var probe = new OutputProbe();
                var settings = OutputSettings(directory) with { VideoEditorPath = CreateFakeEditor(directory) };
                string source = Path.Combine(directory, "recording-source.mp4");
                byte[] bytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
                File.WriteAllBytes(source, bytes);
                await probe.Router(directory).RecordingAsync(source, destination, settings);
                Check(bytes.SequenceEqual(File.ReadAllBytes(source)), "動画原本を変更/移動しない");
                Equal(0, probe.ImageCopies);
                Equal(destination == OutputDestination.Clipboard ? 1 : 0, probe.Files.Count);
                Equal(destination == OutputDestination.ExternalApp ? 1 : 0, probe.Launches.Count);
                if (destination == OutputDestination.File)
                {
                    string saved = Directory.GetFiles(settings.SaveDirectory).Single();
                    Check(source != saved, "原本と別パスへ保存する");
                    Check(bytes.SequenceEqual(File.ReadAllBytes(saved)), "動画全体のバイト列を維持する");
                }
                else Check(!Directory.Exists(settings.SaveDirectory), "選択されていない保存フォルダを作成しない");
                if (destination == OutputDestination.Clipboard) Equal(source, probe.Files[0]);
                if (destination == OutputDestination.ExternalApp) Equal(source, probe.Launches[0].ArgumentList.Single());
                Check(!Directory.Exists(Path.Combine(directory, "work")), "動画原本を不必要に複製しない");
            })));
        }
        Run("動画外部起動失敗は原本を保持・パスを通知", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            string source = Path.Combine(directory, "source.mp4");
            byte[] bytes = [1, 7, 3, 9];
            File.WriteAllBytes(source, bytes);
            var probe = new OutputProbe { FailLaunch = true };
            var settings = OutputSettings(directory) with { VideoEditorPath = CreateFakeEditor(directory) };
            string? failure = null;
            try { await probe.Router(directory).RecordingAsync(source, OutputDestination.ExternalApp, settings); }
            catch (IOException ex) { failure = ex.Message; }
            Check(failure is not null && failure.Contains(source, StringComparison.Ordinal), "原本パスを通知する");
            Check(bytes.SequenceEqual(File.ReadAllBytes(source)), "起動失敗で動画を失わない");
            Check(!Directory.Exists(settings.SaveDirectory), "起動失敗時に勝手に別保存しない");
        })));
        Run("存在しない動画は全出力で副作用前に拒否", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var probe = new OutputProbe();
            var router = probe.Router(directory);
            foreach (OutputDestination destination in Enum.GetValues<OutputDestination>())
            {
                bool rejected = false;
                try { await router.RecordingAsync(Path.Combine(directory, "missing.mp4"), destination, OutputSettings(directory)); }
                catch (FileNotFoundException) { rejected = true; }
                Check(rejected, "欠落動画を拒否する");
            }
            Equal(0, probe.Files.Count);
            Equal(0, probe.Launches.Count);
            Equal(0, Directory.GetFileSystemEntries(directory).Length);
        })));
        Run("外部アプリ引数は空白/記号込みでも単一ArgumentList", () => WithOutputDirectory(directory =>
        {
            string executable = CreateFakeEditor(directory);
            string source = Path.Combine(directory, "動画 & $(ignored) ; [x] = final.mp4");
            ProcessStartInfo start = OutputRouter.EditorStartInfo(executable, source);
            Equal(executable, start.FileName);
            Equal(false, start.UseShellExecute);
            Equal("", start.Arguments);
            Equal(1, start.ArgumentList.Count);
            Equal(Path.GetFullPath(source), start.ArgumentList[0]);
            Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
            foreach (string invalid in new[] { "", "relative.exe", Path.Combine(directory, "missing.exe"), source })
            {
                bool rejected = false;
                try { OutputRouter.ValidateEditor(invalid); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "不正な起動先を拒否する");
            }
        }));
        Run("同時刻動画16並列保存・既存非上書き・原本保持", () => WithOutputDirectory(directory =>
        {
            string folder = Path.Combine(directory, "saved");
            Directory.CreateDirectory(folder);
            var timestamp = new DateTimeOffset(2026, 10, 10, 12, 34, 56, 789, TimeSpan.FromHours(9));
            string existing = Path.Combine(folder, "20261010_123456_789.mp4");
            byte[] sentinel = [9, 8, 7];
            File.WriteAllBytes(existing, sentinel);
            var results = new ConcurrentBag<(string Source, string Target, byte[] Bytes)>();
            Parallel.For(0, 16, index =>
            {
                string source = Path.Combine(directory, $"source-{index}.mp4");
                byte[] bytes = Enumerable.Repeat((byte)index, 32768).ToArray();
                File.WriteAllBytes(source, bytes);
                results.Add((source, OutputRouter.CopyVideo(source, folder, timestamp), bytes));
            });
            Equal(16, results.Select(item => item.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Equal(17, Directory.GetFiles(folder).Length);
            Check(sentinel.SequenceEqual(File.ReadAllBytes(existing)), "先に存在する動画を保持する");
            foreach (var (source, target, bytes) in results)
            {
                Check(bytes.SequenceEqual(File.ReadAllBytes(source)), "動画の原本を保持する");
                Check(bytes.SequenceEqual(File.ReadAllBytes(target)), "保存完了した全バイトを読み出せる");
                Equal(".mp4", Path.GetExtension(target));
            }
        }));
        Run("動画保存失敗は原本/既存データを保持・一時ファイルを残さない", () => WithOutputDirectory(directory =>
        {
            string source = Path.Combine(directory, "source.mp4");
            string folder = Path.Combine(directory, "blocked");
            byte[] bytes = [2, 4, 6, 8];
            File.WriteAllBytes(source, bytes);
            File.WriteAllText(folder, "existing");
            bool rejected = false;
            try { OutputRouter.CopyVideo(source, folder); }
            catch (IOException) { rejected = true; }
            Check(rejected, "保存先がファイルの場合は失敗する");
            Check(bytes.SequenceEqual(File.ReadAllBytes(source)), "失敗時も原本を保持する");
            Equal("existing", File.ReadAllText(folder));
            Equal(2, Directory.GetFileSystemEntries(directory).Length);
        }));
    }

    private static AppSettings OutputSettings(string directory) => new() { SaveDirectory = Path.Combine(directory, "saved") };

    private static Bitmap OutputImage()
    {
        var image = new Bitmap(3, 2);
        image.SetPixel(1, 1, Color.Magenta);
        return image;
    }

    private static string CreateFakeEditor(string directory)
    {
        string folder = Path.Combine(directory, "editor & tools");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "never launched.exe");
        File.WriteAllBytes(file, [0, 1, 2]);
        return file;
    }

    private static void RunOutputTask(Func<Task> action) => Task.Run(action).GetAwaiter().GetResult();

    private static void WithOutputDirectory(Action<string> test)
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(root, $"SSCapture-OutputTests-{Guid.NewGuid():N}"));
        Check(directory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "テスト先は一時フォルダ内であること");
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class OutputProbe
    {
        internal int ImageCopies { get; private set; }
        internal int ImagePixel { get; private set; }
        internal List<string> Files { get; } = [];
        internal List<ProcessStartInfo> Launches { get; } = [];
        internal bool FailImageCopy { get; init; }
        internal bool FailLaunch { get; init; }

        internal OutputRouter Router(string directory) => new(Path.Combine(directory, "work"),
            image =>
            {
                ImageCopies++;
                ImagePixel = image.GetPixel(1, 1).ToArgb();
                return FailImageCopy ? Task.FromException(new IOException("synthetic copy failure")) : Task.CompletedTask;
            },
            file => { Files.Add(file); return Task.CompletedTask; },
            start =>
            {
                Launches.Add(start);
                if (FailLaunch) throw new IOException("synthetic launch failure");
            });
    }
}
