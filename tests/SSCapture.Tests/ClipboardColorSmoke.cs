using System.Drawing.Imaging;
using System.Text.Json;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    // 明示実行専用。実クリップボードを変更する。通常回帰には含めない。
    private static int ClipboardColorSmoke(string directory, string depthText)
    {
        var depth = Enum.Parse<ScreenshotColorDepth>(depthText);
        if (depth == ScreenshotColorDepth.Original) throw new ArgumentException("Indexed8 / Indexed4を指定してください。");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using var source = new Bitmap(512, 256, PixelFormat.Format24bppRgb);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                source.SetPixel(x, y, Color.FromArgb(x % 256, y, (x / 256 * 128 + x / 2 + y / 3) % 256));
        source.Save(Path.Combine(directory, "source.png"), ImageFormat.Png);
        using var form = new Form { ShowInTaskbar = false, Opacity = 0 };
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try
            {
                var settings = new AppSettings { SaveDirectory = directory, ScreenshotColorDepth = depth,
                    KeepHistory = false, OpenFolderAfterSave = false, ScreenshotPaddingPixels = 0 };
                using var router = new OutputRouter(Path.Combine(directory, "work"));
                await router.ScreenshotAsync(source, OutputDestination.Clipboard | OutputDestination.File, settings, depthText + ".png");
                using var copied = Clipboard.GetImage() ?? throw new IOException("画像をコピーできませんでした。");
                copied.Save(Path.Combine(directory, depthText + "-clipboard-readback.png"), ImageFormat.Png);
                var clipboard = Clipboard.GetDataObject() ?? throw new IOException("クリップボードに画像がありません。");
                using var nativePng = clipboard.GetData("PNG", false) as MemoryStream ?? throw new IOException("PNG形式がありません。");
                using var nativeDib = clipboard.GetData(DataFormats.Dib, false) as MemoryStream ?? throw new IOException("DIB形式がありません。");
                var pngBytes = nativePng.ToArray(); var dibBytes = nativeDib.ToArray();
                File.WriteAllBytes(Path.Combine(directory, depthText + "-clipboard-native.png"), pngBytes);
                File.WriteAllBytes(Path.Combine(directory, depthText + "-clipboard-native.dib"), dibBytes);
                int expectedBits = depth == ScreenshotColorDepth.Indexed8 ? 8 : 4;
                if (pngBytes[24] != expectedBits || pngBytes[25] != 3 || BitConverter.ToInt16(dibBytes, 14) != expectedBits)
                    throw new IOException("クリップボードのPNGまたはDIBが指定パレット形式ではありません。");
                var data = new { depth = depthText, width = copied.Width, height = copied.Height,
                    clipboardPixelFormat = copied.PixelFormat.ToString(), formats = Clipboard.GetDataObject()?.GetFormats(false),
                    pngBitDepth = pngBytes[24], pngColorType = pngBytes[25], dibBitDepth = BitConverter.ToInt16(dibBytes, 14),
                    source = "合成画像。実OutputRouter → 実ClipboardWriter。個人設定・画面撮影なし。" };
                File.WriteAllText(Path.Combine(directory, depthText + "-clipboard.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(data));
            }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
}
