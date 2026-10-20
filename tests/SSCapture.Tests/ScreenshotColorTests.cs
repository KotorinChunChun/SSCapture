using System.Drawing.Imaging;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static void ScreenshotColorTests()
    {
        Run("減色なしは画素と透過を変えない独立画像", () =>
        {
            using var source = PaddingSource();
            using var output = ScreenshotColors.Apply(source, ScreenshotColorDepth.Original);
            AssertPadding(source, output, 0);
            Check(!ReferenceEquals(source, output), "所有権が独立していない");
        });
        foreach (var depth in new[] { ScreenshotColorDepth.Indexed8, ScreenshotColorDepth.Indexed4 })
        {
            Run($"コピー用{depth}はパレット形式と不透明画素を保持", () =>
            {
                using var source = ColorSource();
                using var indexed = ScreenshotColors.Apply(source, depth);
                using var copy = ClipboardWriter.PrepareImage(indexed);
                Equal(indexed.PixelFormat, copy.PixelFormat);
                Equal(Color.White.ToArgb(), copy.GetPixel(2, 0).ToArgb());
                Equal((byte)0, indexed.GetPixel(2, 0).A);
                for (int y = 0; y < indexed.Height; y++)
                    for (int x = 0; x < indexed.Width; x++)
                        if (indexed.GetPixel(x, y).A == 255) Equal(indexed.GetPixel(x, y).ToArgb(), copy.GetPixel(x, y).ToArgb());
                AssertColors(copy, depth);
            });
            Run($"コピー用DIB{depth}はヘッダー・パレット・奇数幅を復号可能", () =>
            {
                using var source = new Bitmap(5, 3);
                for (int y = 0; y < 3; y++)
                    for (int x = 0; x < 5; x++) source.SetPixel(x, y, x % 2 == 0 ? Color.Red : Color.Blue);
                using var indexed = ScreenshotColors.Apply(source, depth);
                var dib = ClipboardWriter.EncodeDib(indexed);
                int bits = depth == ScreenshotColorDepth.Indexed8 ? 8 : 4;
                Equal((short)bits, BitConverter.ToInt16(dib, 14));
                Equal(5, BitConverter.ToInt32(dib, 4)); Equal(3, BitConverter.ToInt32(dib, 8));
                using var encoded = new MemoryStream();
                using (var writer = new BinaryWriter(encoded, System.Text.Encoding.UTF8, true))
                {
                    writer.Write((ushort)0x4d42); writer.Write(14 + dib.Length); writer.Write(0);
                    writer.Write(14 + BitConverter.ToInt32(dib, 0) + (1 << bits) * 4); writer.Write(dib);
                }
                encoded.Position = 0;
                using var decoded = new Bitmap(encoded);
                Equal(indexed.PixelFormat, decoded.PixelFormat);
                for (int y = 0; y < 3; y++)
                    for (int x = 0; x < 5; x++) Equal(source.GetPixel(x, y).ToArgb(), decoded.GetPixel(x, y).ToArgb());
            });
            Run($"減色{depth}の少色・透明・奇数幅とPNGビット深度", () =>
            {
                using var source = new Bitmap(5, 3);
                var colors = new[] { Color.Red, Color.Lime, Color.Blue, Color.White, Color.Transparent };
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++) source.SetPixel(x, y, colors[(x + y) % colors.Length]);
                using var output = ScreenshotColors.Apply(source, depth);
                AssertIndexed(output, depth);
                using var encoded = new MemoryStream();
                output.Save(encoded, ImageFormat.Png);
                var data = encoded.ToArray();
                Equal((byte)(depth == ScreenshotColorDepth.Indexed8 ? 8 : 4), data[24]);
                Equal((byte)3, data[25]); // PNG color type 3 = palette
                encoded.Position = 0;
                using var decoded = new Bitmap(encoded);
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++)
                    {
                        var expected = source.GetPixel(x, y);
                        if (expected.A == 0) Equal((byte)0, decoded.GetPixel(x, y).A);
                        else Equal(expected.ToArgb(), decoded.GetPixel(x, y).ToArgb());
                    }
            });
            Run($"減色{depth}は多色画像の上限・白余白・黒・透明・寸法を保持", () =>
            {
                using var source = ColorSource();
                using var output = ScreenshotColors.Apply(source, depth);
                AssertIndexed(output, depth);
                Equal(source.Size, output.Size);
                Equal(Color.White.ToArgb(), output.GetPixel(0, 0).ToArgb());
                Equal(Color.Black.ToArgb(), output.GetPixel(1, 0).ToArgb());
                Equal((byte)0, output.GetPixel(2, 0).A);
                using var again = ScreenshotColors.Apply(source, depth);
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++) Equal(output.GetPixel(x, y).ToArgb(), again.GetPixel(x, y).ToArgb());
            });
            Run($"減色{depth}は保存・外部・コピー・履歴・再送に共通適用", () => WithOutputDirectory(directory => RunOutputTask(async () =>
            {
                using var source = ColorSource();
                int copies = 0, launches = 0, histories = 0;
                string? file = null;
                using var router = new OutputRouter(Path.Combine(directory, "work"), image =>
                {
                    AssertIndexed(image, depth); copies++;
                    Equal(Color.White.ToArgb(), image.GetPixel(0, 0).ToArgb());
                    if (copies == 1) throw new IOException("再送fixture");
                    return Task.CompletedTask;
                }, launch: start =>
                {
                    file = start.ArgumentList.Single();
                    using var image = new Bitmap(file);
                    AssertColors(image, depth);
                    var png = File.ReadAllBytes(file);
                    Equal((byte)(depth == ScreenshotColorDepth.Indexed8 ? 8 : 4), png[24]);
                    Equal((byte)3, png[25]); launches++;
                }, captured: (image, _, _) => { AssertIndexed(image, depth); histories++; });
                var settings = OutputSettings(directory) with
                { ScreenshotColorDepth = depth, ScreenshotPaddingPixels = 1, ImageEditorPath = CreateFakeEditor(directory) };
                try { await router.ScreenshotAsync(source, OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp, settings); }
                catch (IOException) when (copies == 1) { }
                Check(router.HasPendingRetry, "失敗したコピーが再送待ちにならない");
                await router.RetryFailedAsync();
                Equal(2, copies); Equal(1, launches); Equal(1, histories);
                using var saved = new Bitmap(file!);
                Equal(new Size(source.Width + 2, source.Height + 2), saved.Size);
                Equal(PixelFormat.Format32bppArgb, source.PixelFormat);
            })));
        }
        Run("減色設定は旧設定で無効・保存と再読込・不正値拒否", () => WithServiceDirectory(directory =>
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            File.WriteAllText(storePath(), "{}");
            Equal(ScreenshotColorDepth.Original, store.Load(out _).ScreenshotColorDepth);
            foreach (var depth in Enum.GetValues<ScreenshotColorDepth>())
            {
                store.Save(new AppSettings { ScreenshotColorDepth = depth });
                Equal(depth, store.Load(out var warning).ScreenshotColorDepth);
                Equal<string?>(null, warning);
            }
            PaddingRejected(() => new AppSettings { ScreenshotColorDepth = (ScreenshotColorDepth)99 }.Validate());
            string storePath() => Path.Combine(directory, "settings.json");
        }));
        Run("減色なしのコピーは白背景合成と24ビット互換形式を維持", () =>
        {
            using var source = new Bitmap(2, 1);
            source.SetPixel(0, 0, Color.Transparent); source.SetPixel(1, 0, Color.Red);
            using var copy = ClipboardWriter.PrepareImage(source);
            Equal(PixelFormat.Format24bppRgb, copy.PixelFormat);
            Equal(Color.White.ToArgb(), copy.GetPixel(0, 0).ToArgb());
            Equal(Color.Red.ToArgb(), copy.GetPixel(1, 0).ToArgb());
            Equal((byte)0, source.GetPixel(0, 0).A);
        });
        Run("基本タブの減色選択と保存・最小画面で見切れなし", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings(), next => { saved = next; return null; }, () => { }, () => []);
            var field = (ComboBox)form.Controls.Find("ScreenshotColorDepth", true).Single();
            Equal(3, field.Items.Count);
            Equal(0, field.SelectedIndex);
            foreach (int index in new[] { 1, 2, 0 })
            {
                field.SelectedIndex = index;
                typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
                Equal((ScreenshotColorDepth)index, saved!.ScreenshotColorDepth);
            }
            form.Size = form.MinimumSize; _ = form.Handle;
            foreach (var child in Descendants(form)) _ = child.Handle;
            LayoutTree(form);
            Check(field.Parent!.ClientRectangle.Contains(field.Bounds), "減色欄が見切れる");
        });
    }
    private static Bitmap ColorSource()
    {
        var source = new Bitmap(65, 49);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++) source.SetPixel(x, y, Color.FromArgb(255, x * 3, y * 5, (x * 7 + y * 11) % 256));
        source.SetPixel(0, 0, Color.White); source.SetPixel(1, 0, Color.Black); source.SetPixel(2, 0, Color.Transparent);
        return source;
    }
    private static void AssertIndexed(Bitmap image, ScreenshotColorDepth depth)
    {
        Equal(depth == ScreenshotColorDepth.Indexed8 ? PixelFormat.Format8bppIndexed : PixelFormat.Format4bppIndexed, image.PixelFormat);
        AssertColors(image, depth);
    }
    private static void AssertColors(Bitmap image, ScreenshotColorDepth depth)
    {
        var actual = new HashSet<int>();
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) actual.Add(image.GetPixel(x, y).ToArgb());
        Check(actual.Count <= (depth == ScreenshotColorDepth.Indexed8 ? 256 : 16), "指定色数を超えた");
    }
}
