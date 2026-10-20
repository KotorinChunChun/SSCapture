using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal static class ClipboardWriter
{
    public static async Task CopyFileAsync(string path)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA || SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            throw new InvalidOperationException("クリップボードにはUIスレッドからアクセスしてください。");
        if (!File.Exists(path)) throw new FileNotFoundException("動画ファイルがありません。", path);
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { Path.GetFullPath(path) });
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, true, 0, 0); return; }
            catch (ExternalException) when (attempt < 7) { await Task.Delay(50 * (attempt + 1)); }
        }
    }

    public static async Task CopyAsync(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("クリップボードへのコピーはSTAスレッドから実行してください。");
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            throw new InvalidOperationException("クリップボードへのコピーには実行中のUIメッセージループが必要です。");
        using var copy = PrepareImage(image);
        var data = new DataObject();
        using var png = new MemoryStream();
        using var dib = new MemoryStream();
        if (IsPalette(copy))
        {
            // OfficeがPNGを選べばパレットとビット深度をそのまま取り込める。
            // CF_DIBも同じパレットを公開し、PNG非対応の受取先でも減色を保つ。
            copy.Save(png, ImageFormat.Png); png.Position = 0;
            var bytes = EncodeDib(copy);
            dib.Write(bytes); dib.Position = 0;
            data.SetData("PNG", false, png);
            data.SetData(DataFormats.Dib, false, dib);
        }
        data.SetData(DataFormats.Bitmap, true, copy);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true, 0, 0);
                return;
            }
            catch (ExternalException) when (attempt < 7)
            {
                // Await keeps the UI responsive and returns to the same STA context.
                await Task.Delay(50 * (attempt + 1));
            }
        }
    }

    internal static bool IsPalette(Bitmap image) => image.PixelFormat is PixelFormat.Format8bppIndexed or PixelFormat.Format4bppIndexed;

    internal static Bitmap PrepareImage(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (IsPalette(image))
        {
            var result = (Bitmap)image.Clone();
            var palette = result.Palette;
            for (int index = 0; index < palette.Entries.Length; index++)
            {
                var color = palette.Entries[index];
                // 既存のコピーと同じ白背景へ合成する。画素の索引と色数は変えない。
                int White(int value) => (value * color.A + 255 * (255 - color.A) + 127) / 255;
                palette.Entries[index] = Color.FromArgb(White(color.R), White(color.G), White(color.B));
            }
            result.Palette = palette;
            return result;
        }
        var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.Clear(Color.White);
        graphics.DrawImageUnscaled(image, Point.Empty);
        return copy;
    }

    internal static byte[] EncodeDib(Bitmap image)
    {
        using var encoded = new MemoryStream();
        image.Save(encoded, ImageFormat.Bmp);
        // CF_DIBはBITMAPFILEHEADER（14バイト）を含めない。
        var bytes = encoded.ToArray();
        if (bytes.Length < 54 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            throw new IOException("クリップボード用DIBを生成できませんでした。");
        return bytes[14..];
    }
}
