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
        // CF_BITMAP consumers often discard alpha. Flatten a private clone so that
        // transparent freeform pixels paste consistently without altering the PNG.
        using var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(copy))
        {
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(image, Point.Empty);
        }
        var data = new DataObject();
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
}
