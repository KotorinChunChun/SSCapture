namespace SSCapture.Services;

// Consult the original image only for narrow gaps where text geometry alone is ambiguous.
internal sealed class OcrColumnRules
{
    private readonly Bitmap image;
    private readonly int top;
    private readonly int bottom;
    private readonly Dictionary<int, bool> columns = new();

    internal OcrColumnRules(Bitmap image, OcrLineLayout?[] layout)
    {
        this.image = image;
        var boxes = layout.Where(line => line?.Bounds.IsValid == true).Select(line => line!.Bounds).ToArray();
        if (boxes.Length < 2) return;
        top = Math.Clamp((int)boxes.Min(box => box.Top), 0, image.Height - 1);
        bottom = Math.Clamp((int)boxes.Max(box => box.Bottom), top, image.Height - 1);
        if (bottom - top < boxes.Min(box => box.Height) * 1.5f) bottom = top;
    }

    internal bool Between(float right, float left)
    {
        if (top == bottom || left - right < 2) return false;
        int start = Math.Max(2, (int)Math.Ceiling(right));
        int end = Math.Min(image.Width - 3, (int)Math.Floor(left));
        for (int x = start; x <= end; x++)
        {
            if (!columns.TryGetValue(x, out bool ruled))
            {
                if (columns.Count >= 4096) return false;
                columns[x] = ruled = IsRule(x);
            }
            if (ruled) return true;
        }
        return false;
    }

    private bool IsRule(int x)
    {
        int samples = Math.Min(32, bottom - top + 1);
        int dark = 0, light = 0;
        for (int i = 0; i < samples; i++)
        {
            int y = top + (bottom - top) * i / (samples - 1);
            int pixel = Luma(x, y);
            int a = Luma(x - 2, y) - pixel, b = Luma(x + 2, y) - pixel;
            if (Math.Min(a, b) >= 18) dark++;
            if (Math.Max(a, b) <= -18) light++;
        }
        return Math.Max(dark, light) >= samples * 0.85f;
    }

    private int Luma(int x, int y)
    {
        var c = image.GetPixel(x, y);
        return (c.R * 77 + c.G * 150 + c.B * 29) >> 8;
    }
}
