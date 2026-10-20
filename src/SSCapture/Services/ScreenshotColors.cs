using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal enum ScreenshotColorDepth { Original, Indexed8, Indexed4 }

internal static class ScreenshotColors
{
    private sealed class Bucket
    {
        public long Count, A, R, G, B;
        public void Add(Color color) { Count++; A += color.A; R += color.R; G += color.G; B += color.B; }
        public Color Mean => Color.FromArgb((int)(A / Count), (int)(R / Count), (int)(G / Count), (int)(B / Count));
    }
    private sealed class Box(List<Bucket> items)
    {
        public List<Bucket> Items { get; } = items;
        public long Weight { get; } = items.Sum(i => i.Count);
        public (int Channel, int Range) Spread { get; } = Enumerable.Range(0, 4)
            .Select(c => (Channel: c, Range: items.Max(i => Component(i.Mean, c)) - items.Min(i => Component(i.Mean, c))))
            .OrderByDescending(i => i.Range).First();
        public Color Mean => Color.FromArgb((int)(Items.Sum(i => i.A) / Weight), (int)(Items.Sum(i => i.R) / Weight),
            (int)(Items.Sum(i => i.G) / Weight), (int)(Items.Sum(i => i.B) / Weight));
    }
    private static int Component(Color color, int channel) => channel switch { 0 => color.R, 1 => color.G, 2 => color.B, _ => color.A };
    private static int Key(Color c) => (c.A >> 4) << 15 | (c.R >> 3) << 10 | (c.G >> 3) << 5 | (c.B >> 3);
    private static int Argb(byte[] row, int x) => row[x + 3] == 0 ? 0 : Color.FromArgb(row[x + 3], row[x + 2], row[x + 1], row[x]).ToArgb();
    private static bool Reserved(int color) => color is 0 or -1 or unchecked((int)0xff000000);

    // 元画像は変更せず、インデックス形式の独立画像を返す。
    public static Bitmap Apply(Bitmap source, ScreenshotColorDepth depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(depth)) throw new ArgumentException("静止画の減色指定が正しくありません。");
        if (depth == ScreenshotColorDepth.Original) return (Bitmap)source.Clone();
        int limit = depth == ScreenshotColorDepth.Indexed8 ? 256 : 16;
        var histogram = new Dictionary<int, Bucket>();
        var exact = new HashSet<int>();
        var reserved = new HashSet<int>();
        var row = new byte[checked(source.Width * 4)];
        var bounds = new Rectangle(Point.Empty, source.Size);
        var input = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(input.Scan0, y * input.Stride), row, 0, row.Length);
                for (int x = 0; x < row.Length; x += 4)
                {
                    int argb = Argb(row, x);
                    if (exact.Count <= limit) exact.Add(argb);
                    if (Reserved(argb)) { reserved.Add(argb); continue; }
                    var color = Color.FromArgb(argb);
                    int key = Key(color);
                    if (!histogram.TryGetValue(key, out var bin)) histogram[key] = bin = new();
                    bin.Add(color);
                }
            }
            bool lossless = exact.Count <= limit;
            var colors = lossless ? exact.Order().Select(Color.FromArgb).ToList() : reserved.Order().Select(Color.FromArgb).ToList();
            if (!lossless)
            {
                var boxes = new List<Box> { new(histogram.Values.ToList()) };
                while (boxes.Count < limit - colors.Count)
                {
                    var selected = boxes.Where(b => b.Items.Count > 1).OrderByDescending(b => b.Spread.Range * b.Weight).FirstOrDefault();
                    if (selected is null) break;
                    int channel = selected.Spread.Channel;
                    var sorted = selected.Items.OrderBy(i => Component(i.Mean, channel)).ToList();
                    long total = 0;
                    int split = 0;
                    while (split < sorted.Count - 1 && total < selected.Weight / 2) total += sorted[split++].Count;
                    split = Math.Clamp(split, 1, sorted.Count - 1);
                    boxes.Remove(selected);
                    boxes.Add(new(sorted.GetRange(0, split)));
                    boxes.Add(new(sorted.GetRange(split, sorted.Count - split)));
                }
                colors.AddRange(boxes.Select(b => b.Mean));
            }
            var exactIndices = colors.Select((c, i) => (c, i)).GroupBy(p => p.c.ToArgb()).ToDictionary(g => g.Key, g => g.First().i);
            var indices = lossless ? new Dictionary<int, int>() : histogram.ToDictionary(p => p.Key, p => Nearest(p.Value.Mean, colors));
            var format = depth == ScreenshotColorDepth.Indexed8 ? PixelFormat.Format8bppIndexed : PixelFormat.Format4bppIndexed;
            var result = new Bitmap(source.Width, source.Height, format);
            try
            {
                var palette = result.Palette;
                for (int i = 0; i < palette.Entries.Length; i++) palette.Entries[i] = i < colors.Count ? colors[i] : colors[0];
                result.Palette = palette;
                var output = result.LockBits(bounds, ImageLockMode.WriteOnly, format);
                try
                {
                    var packed = new byte[Math.Abs(output.Stride)];
                    for (int y = 0; y < source.Height; y++)
                    {
                        Array.Clear(packed);
                        Marshal.Copy(IntPtr.Add(input.Scan0, y * input.Stride), row, 0, row.Length);
                        for (int x = 0; x < source.Width; x++)
                        {
                            int argb = Argb(row, x * 4);
                            int index = lossless || Reserved(argb) ? exactIndices[argb] : indices[Key(Color.FromArgb(argb))];
                            if (depth == ScreenshotColorDepth.Indexed8) packed[x] = (byte)index;
                            else packed[x / 2] |= (byte)(index << (x % 2 == 0 ? 4 : 0));
                        }
                        Marshal.Copy(packed, 0, IntPtr.Add(output.Scan0, y * output.Stride), packed.Length);
                    }
                }
                finally { result.UnlockBits(output); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { source.UnlockBits(input); }
    }
    private static int Nearest(Color color, List<Color> palette)
    {
        int best = 0, distance = int.MaxValue;
        for (int i = 0; i < palette.Count; i++)
        {
            // 不透明色を透明な予約色へ割り当てない。
            if (palette[i].A == 0 && color.A != 0) continue;
            int a = color.A - palette[i].A, r = color.R - palette[i].R, g = color.G - palette[i].G, b = color.B - palette[i].B;
            int next = a * a * 4 + r * r + g * g + b * b;
            if (next < distance) { best = i; distance = next; }
        }
        return best;
    }
}
