using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SSCapture.Services;

namespace SSCapture.Capture;

internal enum ScrollFrameResult { First, Appended, Unchanged, Unmatched, Limit }

internal sealed class ScrollFrameStitcher
{
    private const int MinimumOverlap = 8;
    private readonly Size viewport;
    private readonly int padding;
    private readonly List<int[]> strips = [];
    private Frame? previous;

    private sealed record Frame(int[] Pixels, ulong[] Rows);
    internal int Height { get; private set; }

    internal ScrollFrameStitcher(Size viewport, int padding)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) throw new ArgumentException("撮影範囲を指定してください。", nameof(viewport));
        if (padding is < 0 or > ScreenshotPadding.MaximumPixels) throw new ArgumentOutOfRangeException(nameof(padding));
        this.viewport = viewport;
        this.padding = padding;
        if (!Fits(viewport.Height))
            throw new ArgumentException("白い余白を含めると、撮影範囲がスクロール撮影の画像上限を超えます。", nameof(viewport));
    }

    internal ScrollFrameResult Add(Bitmap bitmap)
    {
        if (bitmap.Size != viewport) throw new ArgumentException("取得した画面の寸法が変わりました。", nameof(bitmap));

        Frame current = Read(bitmap);
        if (previous is not { } reference)
        {
            previous = current;
            strips.Add(current.Pixels);
            Height = viewport.Height;
            return ScrollFrameResult.First;
        }
        if (reference.Pixels.AsSpan().SequenceEqual(current.Pixels)) return ScrollFrameResult.Unchanged;

        int overlap = FindOverlap(reference, current);
        if (overlap == 0) return ScrollFrameResult.Unmatched;

        int nextHeight = Height + viewport.Height - overlap;
        if (!Fits(nextHeight)) return ScrollFrameResult.Limit;

        strips.Add(current.Pixels.AsSpan(overlap * viewport.Width).ToArray());
        previous = current;
        Height = nextHeight;
        return ScrollFrameResult.Appended;
    }

    private bool Fits(int height)
    {
        long paddedWidth = (long)viewport.Width + padding * 2;
        long paddedHeight = (long)height + padding * 2;
        return paddedWidth <= RegionCoordinates.MaximumDimension && paddedHeight <= RegionCoordinates.MaximumDimension
            && paddedWidth * paddedHeight <= RegionCoordinates.MaximumPixels;
    }

    private int FindOverlap(Frame reference, Frame current)
    {
        // The new frame's prefix must match a suffix of the last accepted frame.
        // Use a prefix table so even a large run of identical rows is scanned in linear time.
        ulong[] pattern = current.Rows;
        int[] prefix = new int[pattern.Length];
        for (int i = 1; i < pattern.Length; i++)
            prefix[i] = Match(pattern, prefix, prefix[i - 1], pattern[i]);

        int length = 0;
        foreach (ulong row in reference.Rows)
        {
            if (length == pattern.Length) length = prefix[length - 1];
            length = Match(pattern, prefix, length, row);
        }
        if (length == pattern.Length) length = prefix[length - 1];

        int firstDifferent = Array.FindIndex(pattern, row => row != pattern[0]);
        if (firstDifferent < 0) return 0;

        int match = 0;
        while (length >= MinimumOverlap && length > firstDifferent)
        {
            int count = length * viewport.Width;
            if (reference.Pixels.AsSpan(reference.Pixels.Length - count).SequenceEqual(current.Pixels.AsSpan(0, count)))
            {
                if (match != 0) return 0;
                match = length;
            }
            length = prefix[length - 1];
        }
        return match;
    }

    private static int Match(ulong[] pattern, int[] prefix, int length, ulong row)
    {
        while (length > 0 && pattern[length] != row) length = prefix[length - 1];
        return pattern[length] == row ? length + 1 : length;
    }

    private static Frame Read(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int[] pixels = GC.AllocateUninitializedArray<int>(width * height);
        ulong[] hashes = new ulong[height];
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                int offset = y * width;
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, offset, width);
                ulong hash = 14695981039346656037;
                for (int x = offset; x < offset + width; x++)
                {
                    // The unused high byte of a 32-bit RGB desktop image can vary by source.
                    pixels[x] &= 0x00ffffff;
                    hash = unchecked((hash ^ (uint)pixels[x]) * 1099511628211);
                }
                hashes[y] = hash;
            }
        }
        finally { bitmap.UnlockBits(data); }
        return new(pixels, hashes);
    }

    internal Bitmap Build()
    {
        if (Height == 0) throw new InvalidOperationException("撮影した画像がありません。");
        var bitmap = new Bitmap(viewport.Width, Height, PixelFormat.Format32bppRgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try
            {
                int row = 0;
                foreach (int[] strip in strips)
                {
                    for (int offset = 0; offset < strip.Length; offset += viewport.Width)
                        Marshal.Copy(strip, offset, data.Scan0 + row++ * data.Stride, viewport.Width);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
