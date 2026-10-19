using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal static class ScreenshotPadding
{
    internal const int MaximumPixels = 4096;

    internal static Size OutputSize(Size source, int pixels)
    {
        if (pixels is < 0 or > MaximumPixels)
            throw new ArgumentException($"画像の余白は0～{MaximumPixels}pxで指定してください。");
        if (source.Width < 1 || source.Height < 1)
            throw new ArgumentException("撮影画像の寸法が不正です。");
        if (pixels == 0) return source;
        long width = (long)source.Width + pixels * 2L, height = (long)source.Height + pixels * 2L;
        if (width > 32768 || height > 32768 || width * height > 100_000_000)
            throw new ArgumentException("余白を含む画像は各辺32768px・合計1億画素以内にしてください。余白を小さくして再撮影してください。");
        return new Size((int)width, (int)height);
    }

    // 元画像は変更せず、選択範囲の外側だけに不透明な白を追加する。
    internal static Bitmap Apply(Bitmap source, int pixels)
    {
        ArgumentNullException.ThrowIfNull(source);
        Size size = OutputSize(source.Size, pixels);
        if (pixels == 0) return (Bitmap)source.Clone();
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(result)) graphics.Clear(Color.White);
            // GDI+の描画による半透明画素の丸めを避け、内部の画素をそのまま複写する。
            var input = source.LockBits(new Rectangle(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var output = result.LockBits(new Rectangle(new Point(pixels, pixels), source.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[checked(source.Width * 4)];
                    for (int y = 0; y < source.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(input.Scan0, y * input.Stride), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(output.Scan0, y * output.Stride), row.Length);
                    }
                }
                finally { result.UnlockBits(output); }
            }
            finally { source.UnlockBits(input); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
