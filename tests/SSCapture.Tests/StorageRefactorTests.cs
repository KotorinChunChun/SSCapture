using System.Text;
using System.Text.Json;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void StorageRefactorTests()
    {
        Run("原子的保存: 新規作成と置換は完成内容を公開しストリームを解放", () => WithServiceDirectory(directory =>
        {
            string folder = Path.Combine(directory, "保存 先");
            string path = Path.Combine(folder, "設定.json");
            Stream? borrowed = null;
            foreach (string content in new[] { "初回の内容", "置換後の内容" })
            {
                AtomicFile.Write(path, stream =>
                {
                    borrowed = stream;
                    Equal(folder, Path.GetDirectoryName(((FileStream)stream).Name));
                    stream.Write(Encoding.UTF8.GetBytes(content));
                });
                Equal(content, File.ReadAllText(path));
                Check(borrowed is { CanWrite: false }, "helper所有のストリームを解放していない");
                Equal(1, Directory.GetFiles(folder).Length);
            }
        }));

        foreach (bool existing in new[] { false, true })
            Run($"原子的保存: writer失敗は既存{existing}の原本と他の一時ファイルを保持", () => WithServiceDirectory(directory =>
            {
                string path = Path.Combine(directory, "settings.json");
                string unrelated = Path.Combine(directory, ".settings.json.other.tmp");
                File.WriteAllText(unrelated, "別処理の一時ファイル");
                if (existing) File.WriteAllText(path, "原本");
                var original = new InvalidOperationException("synthetic writer failure");
                Exception? observed = null;
                Stream? borrowed = null;
                try
                {
                    AtomicFile.Write(path, stream =>
                    {
                        borrowed = stream;
                        stream.Write(Encoding.UTF8.GetBytes("未完成"));
                        throw original;
                    });
                }
                catch (Exception ex) { observed = ex; }
                Check(ReferenceEquals(original, observed), "writerの元の例外が変更された");
                Check(borrowed is { CanWrite: false }, "writer失敗時にストリームを解放していない");
                Equal(existing, File.Exists(path));
                if (existing) Equal("原本", File.ReadAllText(path));
                Equal("別処理の一時ファイル", File.ReadAllText(unrelated));
                Equal(existing ? 2 : 1, Directory.GetFiles(directory).Length);
            }));

        Run("原子的保存: 置換先が使用中でも原本を保持して自分の一時ファイルだけ除去", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            string unrelated = Path.Combine(directory, ".settings.json.other.tmp");
            File.WriteAllText(path, "原本"); File.WriteAllText(unrelated, "保持");
            bool rejected = false;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { AtomicFile.Write(path, stream => stream.Write(Encoding.UTF8.GetBytes("置換禁止"))); }
                catch (IOException) { rejected = true; }
            }
            Check(rejected, "ロックした保存先を置換した");
            Equal("原本", File.ReadAllText(path)); Equal("保持", File.ReadAllText(unrelated));
            Equal(2, Directory.GetFiles(directory).Length);
        }));

        Run("原子的保存: 一時ファイルの清掃失敗でwriterの元の例外を隠さない", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "原本");
            string? temporary = null;
            var original = new InvalidOperationException("synthetic writer failure before cleanup");
            Exception? observed = null;
            try
            {
                try
                {
                    AtomicFile.Write(path, stream =>
                    {
                        temporary = ((FileStream)stream).Name;
                        stream.Write(Encoding.UTF8.GetBytes("未完成"));
                        File.SetAttributes(temporary, FileAttributes.ReadOnly);
                        throw original;
                    });
                }
                catch (Exception ex) { observed = ex; }
                Check(ReferenceEquals(original, observed), "清掃例外で元のwriter例外が置き換わった");
                Check(temporary is not null && File.Exists(temporary), "清掃失敗の条件を作れていない");
                Equal("原本", File.ReadAllText(path));
            }
            finally
            {
                // Reset only the file created by this test so its fixture can be removed.
                if (temporary is not null && File.Exists(temporary)) File.SetAttributes(temporary, FileAttributes.Normal);
            }
        }));

        Run("原子的保存: 設定Schema2と登録範囲のJSON形式・更新往復を維持", () => WithServiceDirectory(directory =>
        {
            var region = RegionCoordinates.FromBounds("登録範囲", new(-1600, 20, 500, 400), RegionLayout);
            var settings = new AppSettings { SaveDirectory = Path.Combine(directory, "画像"), DefaultMode = CaptureMode.Freeform,
                DefaultDestination = OutputDestination.Clipboard | OutputDestination.File, SavedRegions = [region] };
            string settingsPath = Path.Combine(directory, "settings.json"), regionPath = Path.Combine(directory, "last-region.json");
            var settingsStore = new SettingsStore(settingsPath);
            var regionStore = new RegionStore(regionPath);
            settingsStore.Save(settings); regionStore.Save(region);
            settings = settings with { FreezeFrame = false };
            region = region with { Name = "更新後" };
            settingsStore.Save(settings); regionStore.Save(region);
            SettingsEqual(settings, settingsStore.Load(out string? settingsWarning));
            Equal<string?>(null, settingsWarning);
            SavedRegion loaded = regionStore.Load(out string? regionWarning)!;
            Equal<string?>(null, regionWarning);
            Equal(region, loaded with { Layout = region.Layout });
            Check(region.Layout.SequenceEqual(loaded.Layout), "登録範囲の画面情報が変化した");
            using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Equal(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
            Equal("Freeform", json.RootElement.GetProperty("DefaultMode").GetString());
            Equal(JsonValueKind.String, json.RootElement.GetProperty("DefaultDestination").ValueKind);
            Equal(2, Directory.GetFiles(directory).Length);
        }));

        Run("既定PNG命名: 明示した撮影時刻をファイル名と履歴で共用", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var image = OutputImage();
            using var history = new CaptureHistory();
            var capturedAt = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.FromHours(9));
            var settings = OutputSettings(directory) with { FileNamePattern = FileNamePattern.Default };
            int imageCopies = 0, fileCopies = 0, launches = 0;
            var router = new OutputRouter(Path.Combine(directory, "work"),
                _ => { imageCopies++; return Task.CompletedTask; },
                _ => { fileCopies++; return Task.CompletedTask; }, _ => launches++,
                captured: (bitmap, path, time) => history.Add(bitmap, path, time));
            await router.ScreenshotAsync(image, OutputDestination.Clipboard | OutputDestination.File, settings, capturedAt: capturedAt);
            string path = Path.Combine(settings.SaveDirectory, "20010203_040506.png");
            Check(File.Exists(path), "明示撮影時刻が既定ファイル名に使われていない");
            Equal(1, imageCopies); Equal(0, fileCopies); Equal(0, launches);
            Equal(1, history.Entries.Count);
            Equal(capturedAt, history.Entries.Single().CapturedAt);
            Equal(path, history.Entries.Single().SavedPath);
            using var saved = new Bitmap(path);
            Equal(image.GetPixel(1, 1).ToArgb(), saved.GetPixel(1, 1).ToArgb());
        })));
    }
}
