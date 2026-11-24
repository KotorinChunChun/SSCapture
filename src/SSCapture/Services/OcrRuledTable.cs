namespace SSCapture.Services;

// Use visible cell boundaries before inferring columns from the width of their contents.
internal static class OcrRuledTable
{
    internal static string? Read(Bitmap image, string[] lines, OcrLineLayout?[] layout, int limit)
    {
        if (image.Width < 8 || image.Height < 6) return null;
        var verticals = FindRules(image, vertical: true);
        if (verticals.Count == 0) return null;
        var horizontals = FindRules(image, vertical: false);
        if (horizontals.Count == 0) return null;
        int[] xs = CompleteAxis(image, verticals, horizontals, vertical: true);
        if (xs.Length < 3) return null;
        int[] ys = CompleteAxis(image, horizontals, verticals, vertical: false);
        int columns = xs.Length - 1, rows = ys.Length - 1;
        if (rows < 1 || (long)rows * columns > limit) return null;

        var values = new string?[rows, columns];
        // Preserve reading order within wrapped cells. Empty cells, including whole
        // blank columns, are represented by their grid position.
        foreach (int i in Enumerable.Range(0, lines.Length)
            .Where(i => !string.IsNullOrWhiteSpace(lines[i]))
            .OrderBy(i => layout[i]?.Bounds.Top).ThenBy(i => layout[i]?.Bounds.Left))
        {
            string text = lines[i].Trim();
            if (layout[i] is not { Bounds.IsValid: true } line || text.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0)
                return null;
            int row = Band(ys, line.Bounds.Top, line.Bounds.Bottom);
            if (row < 0) return null;
            int col = Band(xs, line.Bounds.Left, line.Bounds.Right);
            if (col >= 0)
            {
                Append(row, col, text);
                continue;
            }

            // One OCR line can span several cells. Slice the original string
            // to retain punctuation and spaces between words in the same cell.
            if (line.Words is not { Length: > 0 } words) return null;
            int start = 0, offset = 0, previous = -1;
            foreach (var word in words.OrderBy(w => w.Bounds.Left))
            {
                if (!word.Bounds.IsValid || string.IsNullOrWhiteSpace(word.Text)
                    || Band(ys, word.Bounds.Top, word.Bounds.Bottom) != row) return null;
                col = Band(xs, word.Bounds.Left, word.Bounds.Right);
                if (col < 0) return null;
                if (col != previous && previous >= 0) Append(row, previous, text[start..offset]);
                while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
                if (col != previous) start = offset;
                if (!text.AsSpan(offset).StartsWith(word.Text, StringComparison.Ordinal)) return null;
                offset += word.Text.Length;
                previous = col;
            }
            if (offset != text.Length) return null;
            Append(row, previous, text[start..offset]);
        }

        return string.Join(Environment.NewLine, Enumerable.Range(0, rows)
            .Select(r => string.Join('\t', Enumerable.Range(0, columns).Select(c => values[r, c]))));

        void Append(int row, int column, string text) =>
            values[row, column] = values[row, column] is string prior ? prior + " " + text : text;
    }

    // Reject text crossing a divider rather than silently assigning it to a wrong cell.
    private static int Band(int[] edges, float start, float end)
    {
        int index = Array.BinarySearch(edges, (int)MathF.Floor((start + end) / 2));
        if (index < 0) index = ~index - 1;
        return index >= 0 && index + 1 < edges.Length && start >= edges[index] - 1 && end <= edges[index + 1] + 1
            ? index : -1;
    }

    private static int[] CompleteAxis(Bitmap image, List<int> rules, List<int> perpendicular, bool vertical)
    {
        int size = vertical ? image.Width : image.Height;
        var edges = new List<int>(rules);
        // A missing outer border is common in Excel. Count the band up to the
        // crop edge only when a perpendicular ruling continues into that band.
        if (edges[0] <= 3) edges[0] = 0;
        else if (HasCrossing(edges[0] / 2)) edges.Insert(0, 0);
        if (edges[^1] >= size - 4) edges[^1] = size;
        else if (HasCrossing((edges[^1] + size) / 2)) edges.Add(size);
        return edges.ToArray();

        bool HasCrossing(int along) => perpendicular.Any(at => Contrast(image, !vertical, at, along) != 0);
    }

    private static List<int> FindRules(Bitmap image, bool vertical)
    {
        int width = vertical ? image.Width : image.Height;
        int height = vertical ? image.Height : image.Width;
        var edges = new List<int>();
        int runStart = -1;
        for (int x = 0; x <= width; x++)
        {
            if (x < width && IsRule(x))
            {
                if (runStart < 0) runStart = x;
                continue;
            }
            if (runStart < 0) continue;
            edges.Add((runStart + x - 1) / 2);
            runStart = -1;
        }
        return edges;

        bool IsRule(int x)
        {
            int samples = Math.Min(40, height);
            int required = (int)Math.Ceiling(samples * 0.85);
            int dark = 0, light = 0;
            for (int i = 0; i < samples; i++)
            {
                int y = (int)((long)i * (height - 1) / (samples - 1));
                int contrast = Contrast(image, vertical, x, y);
                if (contrast > 0) dark++;
                if (contrast < 0) light++;
                if (Math.Max(dark, light) >= required) return true;
                if (Math.Max(dark, light) + samples - i - 1 < required) return false;
            }
            return false;
        }
    }

    private static int Contrast(Bitmap image, bool vertical, int at, int along)
    {
        int size = vertical ? image.Width : image.Height;
        int pixel = Luma(at);
        // At a crop edge sample the interior at two distances. A pale margin
        // adjacent to a real rule should not itself look like a light rule.
        int left = Luma(at >= 3 ? at - 3 : Math.Min(size - 1, at + 5)) - pixel;
        int right = Luma(at + 3 < size ? at + 3 : Math.Max(0, at - 5)) - pixel;
        if (Math.Min(left, right) >= 12) return 1;
        if (Math.Max(left, right) <= -12) return -1;
        return 0;

        int Luma(int x)
        {
            Color color = vertical ? image.GetPixel(x, along) : image.GetPixel(along, x);
            return (77 * color.R + 150 * color.G + 29 * color.B) >> 8;
        }
    }
}
