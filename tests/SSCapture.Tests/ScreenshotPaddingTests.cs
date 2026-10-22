using System.Drawing.Imaging;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void ScreenshotPaddingTests()
    {
        Run("白余白0pxは元の寸法・画素・透過を保持する独立画像", () =>
        {
            using var source = PaddingSource();
            using var result = ScreenshotPadding.Apply(source, 0);
            Equal(source.Size, result.Size);
            Check(!ReferenceEquals(source, result), "元画像を返却した");
            AssertPadding(source, result, 0);
            result.SetPixel(0, 0, Color.Blue);
            Equal(Color.Red.ToArgb(), source.GetPixel(0, 0).ToArgb());
        });
        Run("白余白は上下左右同じ物理pxを外側だけへ追加し元画像を変えない", () =>
        {
            using var source = PaddingSource();
            foreach (int pixels in new[] { 1, 2, 17 })
            {
                using var result = ScreenshotPadding.Apply(source, pixels);
                AssertPadding(source, result, pixels);
            }
            Equal(new Size(3, 2), source.Size);
            Equal(Color.Red.ToArgb(), source.GetPixel(0, 0).ToArgb());
        });
        Run("白余白は24bit画像にも同じ幅で追加", () =>
        {
            using var source = new Bitmap(1, 1, PixelFormat.Format24bppRgb);
            source.SetPixel(0, 0, Color.Lime);
            using var result = ScreenshotPadding.Apply(source, 1);
            AssertPadding(source, result, 1);
        });
        Run("白余白の寸法上限は割当て前に検査し0pxに新たな制約を加えない", () =>
        {
            Equal(new Size(32768, 3), ScreenshotPadding.OutputSize(new Size(32766, 1), 1));
            Equal(new Size(10000, 10000), ScreenshotPadding.OutputSize(new Size(9998, 9998), 1));
            Equal(new Size(8193, 8193), ScreenshotPadding.OutputSize(new Size(1, 1), 4096));
            Equal(new Size(50000, 50000), ScreenshotPadding.OutputSize(new Size(50000, 50000), 0));
            foreach (var (size, pixels) in new[] { (new Size(1, 1), -1), (new Size(1, 1), 4097),
                (new Size(32767, 1), 1), (new Size(9999, 9998), 1), (new Size(int.MaxValue, 1), 1), (Size.Empty, 1) })
                PaddingRejected(() => ScreenshotPadding.OutputSize(size, pixels));
        });
        Run("白余白は旧設定で0px・保存と再読込で同じ値", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            File.WriteAllText(path, "{}");
            Equal(0, store.Load(out var warning).ScreenshotPaddingPixels);
            Equal<string?>(null, warning);
            foreach (int pixels in new[] { 0, 1, 20, 4096 })
            {
                var settings = new AppSettings { ScreenshotPaddingPixels = pixels };
                store.Save(settings);
                SettingsEqual(settings, store.Load(out warning));
                Equal<string?>(null, warning);
            }
        }));
        Run("白余白の不正設定を拒否し元の設定ファイルは保持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            foreach (int pixels in new[] { -1, 4097, int.MaxValue })
                PaddingRejected(() => new AppSettings { ScreenshotPaddingPixels = pixels }.Validate());
            foreach (string value in new[] { "-1", "4097", "1.5", "null", "\"20\"" })
            {
                string json = "{\"ScreenshotPaddingPixels\":" + value + "}";
                File.WriteAllText(path, json);
                Equal(0, store.Load(out var warning).ScreenshotPaddingPixels);
                Check(!string.IsNullOrEmpty(warning), "不正余白設定の警告がない");
                Equal(json, File.ReadAllText(path));
            }
        }));
        Run("基本設定の白余白は整数pxで保存し初期値を復元", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings { ScreenshotPaddingPixels = 12 }, next => { saved = next; return null; }, () => { }, () => []);
            var input = (NumericUpDown)form.Controls.Find("ScreenshotPaddingPixels", true).Single();
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            Equal("基本", tabs.TabPages[0].Text);
            Check(tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "入力プリセット").Contains(input), "余白欄が入力プリセットにない");
            Equal(12m, input.Value); Equal(0m, input.Minimum); Equal(4096m, input.Maximum);
            Equal(0, input.DecimalPlaces); Equal(1m, input.Increment);
            foreach (int pixels in new[] { 0, 20, 4096 })
            {
                input.Value = pixels;
                typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
                Equal(pixels, saved!.ScreenshotPaddingPixels);
            }
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var child in Descendants(form)) _ = child.Handle;
            LayoutTree(form);
            Check(input.Height >= 24 && input.Parent!.ClientRectangle.Contains(input.Bounds), "余白欄が見切れる");
            var basic = (TableLayoutPanel)input.Parent!;
            Equal(4, basic.GetRow(input));
            form.Height += 500;
            LayoutTree(form);
            var label = basic.GetControlFromPosition(0, basic.GetRow(input))!;
            Check(Math.Abs(label.Top + label.Height / 2 - (input.Top + input.Height / 2)) <= 8,
                "画面を高くすると余白ラベルと入力欄が離れる");
        });
        Run("白余白はコピー・保存・外部アプリ・履歴に共通で適用し撮影座標を変えない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var source = PaddingSource();
            using var history = new CaptureHistory();
            int copies = 0, launches = 0;
            string? savedPath = null;
            var bounds = new Rectangle(-200, 50, source.Width, source.Height);
            var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 2, ImageEditorPath = CreateFakeEditor(directory) };
            var router = new OutputRouter(Path.Combine(directory, "work"), image =>
            {
                AssertPadding(source, image, 2); copies++;
                return Task.CompletedTask;
            }, launch: start =>
            {
                savedPath = start.ArgumentList.Single();
                using var image = new Bitmap(savedPath);
                AssertPadding(source, image, 2); launches++;
            }, capturedWithBounds: (image, path, time, area) => history.Add(image, path, time, area));
            await router.ScreenshotAsync(source, OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, settings, captureBounds: bounds);
            Equal(1, copies); Equal(1, launches); Equal(1, history.Entries.Count);
            Equal<Rectangle?>(bounds, history.Entries[0].CaptureBounds);
            Equal(savedPath, history.Entries[0].SavedPath);
            using var fromHistory = history.CloneImage(history.Entries[0].Id);
            AssertPadding(source, fromHistory!, 2);
            using var fromFile = new Bitmap(savedPath!);
            AssertPadding(source, fromFile, 2);
            Equal(new Size(3, 2), source.Size);
        })));
        Run("ファイル未保存でも外部アプリへ白余白付き画像を渡せる", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var source = PaddingSource();
            int launches = 0;
            var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 1, ImageEditorPath = CreateFakeEditor(directory) };
            var router = new OutputRouter(Path.Combine(directory, "work"), launch: start =>
            {
                using var image = new Bitmap(start.ArgumentList.Single());
                AssertPadding(source, image, 1); launches++;
            });
            await router.ScreenshotAsync(source, OutputDestination.ExternalApp, settings);
            Equal(1, launches);
            Check(!Directory.Exists(settings.SaveDirectory), "ファイル保存OFFなのに保存先を作った");
        })));
        Run("余白込みの画像が上限を超える場合は出力や履歴更新より前に拒否", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var source = new Bitmap(10000, 1);
            int copies = 0, histories = 0;
            var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 4096 };
            var router = new OutputRouter(Path.Combine(directory, "work"), _ => { copies++; return Task.CompletedTask; },
                captured: (_, _, _) => histories++);
            bool rejected = false;
            try { await router.ScreenshotAsync(source, OutputDestination.Clipboard | OutputDestination.File, settings); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "巨大な余白画像を受理した");
            Equal(0, copies); Equal(0, histories);
            Check(!Directory.Exists(settings.SaveDirectory), "拒否前に保存先を作った");
        })));
        Run("静止画の余白設定は録画ファイルの内容を変更しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            string source = Path.Combine(directory, "recording.mp4");
            byte[] data = [1, 3, 5, 7];
            File.WriteAllBytes(source, data);
            var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 4096 };
            var router = new OutputRouter(Path.Combine(directory, "work"));
            await router.RecordingAsync(source, OutputDestination.File, settings, fileName: "saved.mp4");
            Check(data.SequenceEqual(File.ReadAllBytes(Path.Combine(settings.SaveDirectory, "saved.mp4"))), "録画の内容が変わった");
        })));
        Run("履歴画像の外部再送は撮影時の余白を増やさない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            using var source = PaddingSource();
            using var padded = ScreenshotPadding.Apply(source, 2);
            var settings = OutputSettings(directory) with { ScreenshotPaddingPixels = 0, KeepHistory = false, ImageEditorPath = CreateFakeEditor(directory) };
            int launches = 0;
            var router = new OutputRouter(Path.Combine(directory, "work"), launch: start =>
            {
                using var image = new Bitmap(start.ArgumentList.Single());
                AssertPadding(source, image, 2); launches++;
            });
            await router.ScreenshotAsync(padded, OutputDestination.ExternalApp, settings);
            Equal(1, launches);
        })));
    }

    private static Bitmap PaddingSource()
    {
        var image = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
        image.SetPixel(0, 0, Color.Red);
        image.SetPixel(1, 0, Color.FromArgb(128, 12, 34, 56));
        image.SetPixel(2, 0, Color.FromArgb(0, 0, 0, 0));
        image.SetPixel(0, 1, Color.Blue); image.SetPixel(1, 1, Color.Lime); image.SetPixel(2, 1, Color.Magenta);
        return image;
    }

    private static void AssertPadding(Bitmap source, Bitmap result, int pixels)
    {
        Equal(new Size(source.Width + pixels * 2, source.Height + pixels * 2), result.Size);
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                bool inside = x >= pixels && x < pixels + source.Width && y >= pixels && y < pixels + source.Height;
                int expected = inside ? source.GetPixel(x - pixels, y - pixels).ToArgb() : Color.White.ToArgb();
                Equal(expected, result.GetPixel(x, y).ToArgb());
            }
    }

    private static void PaddingRejected(Action action)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "不正な余白を受理した");
    }
}
