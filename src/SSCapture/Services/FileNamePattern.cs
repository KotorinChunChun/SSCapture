using System.Globalization;
using System.Text.RegularExpressions;

namespace SSCapture.Services;

internal static partial class FileNamePattern
{
    internal const string Default = "{yyyymmdd}_{hhmmss}.png";
    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Token();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();
    internal static bool UsesOcr(string pattern) => pattern.Contains("{ocr}", StringComparison.Ordinal);

    internal static string Expand(string pattern, DateTimeOffset capturedAt, CaptureIntent intent, string ocr = "", int sequence = 1, string region = "撮影")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        // Validate the pattern separately so braces in recognized text remain literal.
        string skeleton = Token().Replace(pattern, match => match.Groups[1].Value switch
        {
            "yyyymmdd" or "hhmmss" or "ocr" or "#" or "##" or "region" => "x",
            _ => throw new ArgumentException($"未対応のファイル名変数: {match.Value}")
        });
        if (skeleton.Contains('{') || skeleton.Contains('}')) throw new ArgumentException("ファイル名変数の括弧が正しくありません。");
        ValidateFileName(skeleton);
        // Remove only the optional OCR suffix in the template, never underscores
        // that belong to literal text or to another expanded token.
        if (ocr.Length == 0)
        {
            int stemEnd = pattern.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || pattern.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? pattern.Length - 4 : pattern.Length;
            if (pattern[..stemEnd].EndsWith("_{ocr}", StringComparison.Ordinal))
                pattern = pattern.Remove(stemEnd - "_{ocr}".Length, "_{ocr}".Length);
        }
        string name = Token().Replace(pattern, match => match.Groups[1].Value switch
        {
            "yyyymmdd" => capturedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "hhmmss" => capturedAt.ToString("HHmmss", CultureInfo.InvariantCulture),
            "ocr" => ocr,
            "region" => CleanRegion(region),
            "#" => sequence.ToString(CultureInfo.InvariantCulture),
            "##" => sequence.ToString("D2", CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("未対応のファイル名変数です。")
        });
        string extension = intent == CaptureIntent.Screenshot ? ".png" : ".mp4";
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("ファイル名に使う文字列を入力してください。");
        name += extension;
        ValidateFileName(name);
        return name;
    }

    internal static void Validate(string pattern, string separator)
    {
        ValidateSeparator(separator);
        _ = Expand(pattern, DateTimeOffset.Now, CaptureIntent.Screenshot, "OCR");
        _ = Expand(pattern, DateTimeOffset.Now, CaptureIntent.Recording, "OCR");
    }

    internal static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith(' ') || name.EndsWith('.') || name is "." or ".." || Reserved().IsMatch(name))
            throw new ArgumentException("ファイル名が不正です。パス区切り・予約名・末尾の空白/ピリオドは使用できません（240文字以内）。");
    }

    internal static void ValidateSeparator(string separator)
    {
        if (separator is null || separator.Length > 16 || separator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("OCRの区切り文字はファイル名に使える16文字以内で指定してください。");
    }

    internal static string CleanOcr(string text) => new((text ?? "").Where(c => c != ' ' && !Path.GetInvalidFileNameChars().Contains(c)).ToArray());

    internal static string CleanRegion(string text)
    {
        string name = new string((text ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) name = "範囲";
        if (Reserved().IsMatch(name)) name = "_" + name;
        return name;
    }

    internal static string JoinOcr(IEnumerable<string> lines, string separator)
    {
        ValidateSeparator(separator);
        string[] selected = lines.Select(CleanOcr).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (selected.Length > 3) throw new ArgumentException("OCR文字列は3個まで選択できます。");
        return string.Join(separator, selected);
    }
}

// Keep the original pattern separate from OCR literals. Resolve the sequence only
// when publishing the complete file, so concurrent captures never overwrite it.
internal sealed record FileNamePlan(string Pattern, DateTimeOffset CapturedAt, CaptureIntent Intent, string Ocr = "", string Region = "撮影")
{
    internal string Candidate(int collision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(collision);
        bool numbered = Pattern.Contains("{#}", StringComparison.Ordinal) || Pattern.Contains("{##}", StringComparison.Ordinal);
        string name = FileNamePattern.Expand(Pattern, CapturedAt, Intent, Ocr, checked(collision + 1), Region);
        if (!numbered && collision > 0) name = name[..^4] + $"_{collision:D4}" + name[^4..];
        FileNamePattern.ValidateFileName(name);
        return name;
    }
}
