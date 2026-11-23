namespace SSCapture.Services;

internal readonly record struct OcrBounds(float Left, float Top, float Right, float Bottom)
{
    internal float Height => Bottom - Top;
    internal float CenterY => Top + Height / 2;
    internal bool IsValid => float.IsFinite(Left) && float.IsFinite(Top) && float.IsFinite(Right) && float.IsFinite(Bottom)
        && Left >= 0 && Top >= 0 && Right > Left && Bottom > Top;

    internal OcrBounds Union(OcrBounds other) => new(MathF.Min(Left, other.Left), MathF.Min(Top, other.Top),
        MathF.Max(Right, other.Right), MathF.Max(Bottom, other.Bottom));
}

internal sealed record OcrWord(string Text, OcrBounds Bounds);
internal sealed record OcrLineLayout(OcrBounds Bounds, OcrWord[]? Words);

internal enum OcrTableStatus { NotRequested, Ready, Empty, RecognitionFailed, PositionUnavailable, LowConfidence }
internal sealed record OcrTableResult(OcrTableStatus Status, string? Tsv = null);
internal sealed record OcrCopyResult(string Text, string? Error, OcrLineLayout?[]? Layout, OcrTableResult Table);

internal static class OcrCopyService
{
    private sealed record Cell(string Text, OcrBounds Bounds);
    private const int MaxTableCells = 100_000;

    internal static async Task<OcrCopyResult> RecognizeAsync(Bitmap image, string configured, CancellationToken token, bool asTable = true)
    {
        // Keep PNG encoding and engine discovery off the UI thread. The caller owns the image until completion.
        var recognition = await Task.Run(() => OneOcr.RecognizeForCopyAsync(image, configured, asTable, token), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var result = CreateCopyResult(recognition, asTable);
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static OcrCopyResult CreateCopyResult(OcrResult recognition, bool asTable)
    {
        string text = recognition.Error is null ? string.Join(Environment.NewLine, recognition.Lines) : "";
        var table = asTable ? ConvertTable(recognition) : new OcrTableResult(OcrTableStatus.NotRequested);
        return new(text, recognition.Error, recognition.Layout, table);
    }

    internal static OcrTableResult ConvertTable(OcrResult recognition)
    {
        if (recognition.Error is not null) return new(OcrTableStatus.RecognitionFailed);
        if (recognition.Lines.All(string.IsNullOrWhiteSpace)) return new(OcrTableStatus.Empty);
        if (recognition.Layout is not { } layout || layout.Length != recognition.Lines.Length)
            return new(OcrTableStatus.PositionUnavailable);

        var cells = new List<Cell>();
        for (int index = 0; index < layout.Length; index++)
        {
            string text = recognition.Lines[index].Trim();
            if (text.Length == 0) continue;
            if (layout[index] is not { Bounds.IsValid: true, Words: { Length: > 0 } words }
                || words.Any(word => !word.Bounds.IsValid || string.IsNullOrWhiteSpace(word.Text)))
                return new(OcrTableStatus.PositionUnavailable);
            if (!TryAddCells(text, words, cells)) return new(OcrTableStatus.LowConfidence);
        }

        return BuildTable(cells);
    }

    private static bool TryAddCells(string text, OcrWord[] recognized, List<Cell> cells)
    {
        var words = recognized.OrderBy(word => word.Bounds.Left).ToArray();
        float height = MedianHeight(words.Select(word => word.Bounds));
        if (words.Max(word => word.Bounds.CenterY) - words.Min(word => word.Bounds.CenterY) > height * 0.65f)
            return false;

        // Ordinary spaces stay inside a cell. An OCR line can span cells across wider gutters.
        float gap = MathF.Max(3, height * 0.8f);
        var starts = new List<int> { 0 };
        float right = words[0].Bounds.Right;
        for (int index = 1; index < words.Length; index++)
        {
            if (words[index].Bounds.Left - right >= gap) starts.Add(index);
            right = MathF.Max(right, words[index].Bounds.Right);
        }
        if (starts.Count == 1)
        {
            OcrBounds bounds = words.Select(word => word.Bounds).Aggregate((left, next) => left.Union(next));
            return TryAddCell(text, bounds, cells);
        }

        // Recover each cell from the original OCR line so punctuation and spaces inside cells are retained.
        // If the engine's word sequence cannot account for the entire line, avoid emitting a partial table.
        starts.Add(words.Length);
        int offset = 0;
        for (int group = 0; group < starts.Count - 1; group++)
        {
            while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
            int start = offset;
            OcrBounds bounds = words[starts[group]].Bounds;
            for (int index = starts[group]; index < starts[group + 1]; index++)
            {
                while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
                if (!text.AsSpan(offset).StartsWith(words[index].Text, StringComparison.Ordinal)) return false;
                offset += words[index].Text.Length;
                bounds = bounds.Union(words[index].Bounds);
            }
            if (!TryAddCell(text[start..offset], bounds, cells)) return false;
        }
        return offset == text.Length;
    }

    private static bool TryAddCell(string text, OcrBounds bounds, List<Cell> cells)
    {
        if (text.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0 || cells.Count == MaxTableCells) return false;
        cells.Add(new(text, bounds));
        return true;
    }

    private static OcrTableResult BuildTable(List<Cell> cells)
    {
        float height = MedianHeight(cells.Select(cell => cell.Bounds));
        var positioned = new List<(Cell Cell, int Row)>(cells.Count);
        int rows = 0;
        float rowCenter = float.NegativeInfinity;
        foreach (var cell in cells.OrderBy(cell => cell.Bounds.CenterY))
        {
            float distance = cell.Bounds.CenterY - rowCenter;
            if (distance > height * 0.5f)
            {
                if (distance < height * 0.85f) return new(OcrTableStatus.LowConfidence);
                rows++;
                rowCenter = cell.Bounds.CenterY;
            }
            positioned.Add((cell, rows - 1));
        }
        if (rows < 2) return new(OcrTableStatus.LowConfidence);

        // Text with the same left, center, or right alignment occupies a common horizontal band.
        // Use all rows to find the bands, including rows with missing cells.
        var columns = new List<Dictionary<int, string>>();
        float right = float.NegativeInfinity;
        foreach (var (cell, row) in positioned.OrderBy(item => item.Cell.Bounds.Left))
        {
            if (cell.Bounds.Left - right >= MathF.Max(2, height * 0.65f)) columns.Add(new());
            if (!columns[^1].TryAdd(row, cell.Text)) return new(OcrTableStatus.LowConfidence);
            right = MathF.Max(right, cell.Bounds.Right);
        }
        if (columns.Count < 2 || (long)rows * columns.Count > MaxTableCells) return new(OcrTableStatus.LowConfidence);

        var rowCounts = new int[rows];
        foreach (var column in columns)
        foreach (int row in column.Keys)
            rowCounts[row]++;

        // Require evidence of columns appearing together and recurring in the OCR. This filters
        // staggered paragraphs and isolated lines that would create unsupported columns.
        if (rowCounts.Max() < 2 || columns.All(column => column.Count < 2)
            || (rowCounts.Max() != columns.Count && columns.Any(column => column.Count < 2)))
            return new(OcrTableStatus.LowConfidence);

        return new(OcrTableStatus.Ready, string.Join(Environment.NewLine, Enumerable.Range(0, rows)
            .Select(row => string.Join('\t', columns.Select(column => column.GetValueOrDefault(row, ""))))));
    }

    private static float MedianHeight(IEnumerable<OcrBounds> bounds)
    {
        var heights = bounds.Select(box => box.Height).Order().ToArray();
        return heights[heights.Length / 2];
    }
}
