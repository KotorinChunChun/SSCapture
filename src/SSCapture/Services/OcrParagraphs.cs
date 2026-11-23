using System.Text;

namespace SSCapture.Services;

internal static class OcrParagraphs
{
    internal static string Format(OcrResult recognition)
    {
        var lines = recognition.Lines;
        if (recognition.Layout is not { } layout || layout.Length != lines.Length)
            return string.Join(Environment.NewLine, lines);

        var result = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool bullet = IsBullet(line);
            if (i > 0)
            {
                string previous = lines[i - 1];
                bool join = false;
                if (!string.IsNullOrWhiteSpace(previous) && !string.IsNullOrWhiteSpace(line)
                    && layout[i - 1] is { Bounds.IsValid: true } upper && layout[i] is { Bounds.IsValid: true } lower)
                {
                    float height = upper.Bounds.Height;
                    float gap = lower.Bounds.Top - upper.Bounds.Bottom;
                    if (gap > height * 1.3f && previous.Any(IsJapanese) && line.Any(IsJapanese)) result.AppendLine();
                    float indent = lower.Bounds.Left - upper.Bounds.Left;
                    bool aligned = Math.Abs(indent) <= height * 0.65f
                        || (IsBullet(previous) && indent >= 0 && indent <= height * 2);
                    // Estimate the local right margin from nearby lines in the same text column.
                    float right = upper.Bounds.Right;
                    for (int j = Math.Max(0, i - 4); j < Math.Min(lines.Length, i + 4); j++)
                    {
                        if (layout[j] is { Bounds.IsValid: true } neighbour
                            && Math.Abs(neighbour.Bounds.Left - upper.Bounds.Left) <= height * 2
                            && Math.Abs(neighbour.Bounds.Top - upper.Bounds.Top) < height * 7)
                            right = Math.Max(right, neighbour.Bounds.Right);
                    }
                    join = !bullet && !IsNumberedItem(line) && !line.StartsWith("- ", StringComparison.Ordinal) && aligned && gap >= -height * 0.2f && gap <= height * 0.85f
                        && lower.Bounds.Height / height is >= 0.75f and <= 1.33f
                        && upper.Bounds.Right - upper.Bounds.Left >= 5 * height
                        && right - upper.Bounds.Right <= height * 1.5f
                        && !char.IsWhiteSpace(previous[^1]) && !char.IsWhiteSpace(line[0])
                        && !"。！？：:」』".Contains(previous[^1])
                        && (IsJapanese(previous[^1]) || IsJapanese(line[0]));
                }
                if (!join) result.AppendLine();
            }
            if (bullet) result.Append('・').Append(line.AsSpan(1));
            else result.Append(line);
        }
        return result.ToString();
    }

    private static bool IsBullet(string text) => text.Length > 1 && text[0] is '●' or '•' or '·' or '・'
        && char.IsWhiteSpace(text[1]);

    private static bool IsNumberedItem(string text)
    {
        int digits = 0;
        while (digits < text.Length && char.IsDigit(text[digits])) digits++;
        return digits > 0 && digits + 1 < text.Length && text[digits] is '.' or ')' or '）' && char.IsWhiteSpace(text[digits + 1]);
    }

    private static bool IsJapanese(char value) => value is >= 'ぁ' and <= 'ヿ' or >= '㐀' and <= '鿿' or '、' or '。';
}
