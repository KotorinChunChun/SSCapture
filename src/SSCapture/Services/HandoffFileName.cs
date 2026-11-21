namespace SSCapture.Services;

internal sealed class HandoffFileName(FileNamePlan plan, string? fileName, string ocrDirectory, bool confirmed = false,
    Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognize = null)
{
    private readonly object gate = new();
    private Task<string>? ocr;
    internal FileNamePlan Plan { get; } = plan;
    internal string? FileName { get; } = fileName;
    internal bool NeedsOcr => FileName is null && !confirmed && string.IsNullOrWhiteSpace(Plan.Ocr)
        && FileNamePattern.UsesOcr(Plan.Pattern);

    internal async Task<FileNamePlan?> ResolveAsync(Bitmap image)
    {
        if (FileName is not null) return null;
        if (!NeedsOcr) return Plan;
        Task<string> pending;
        lock (gate) pending = ocr ??= Task.Run(() => RecognizeAsync(image));
        return Plan with { Ocr = await pending.ConfigureAwait(false) };
    }

    internal static FileNamePlan? CachePlan(FileNamePlan? plan)
    {
        if (plan is null) return null;
        try { _ = plan.Candidate(0); return plan; }
        catch (ArgumentException)
        {
            // 旧版では未保存出力の命名欄を検証していなかった。受け渡しには既定名を使う。
            return plan with { Pattern = FileNamePattern.Default };
        }
    }

    private async Task<string> RecognizeAsync(Bitmap image)
    {
        try
        {
            OcrResult result = await (recognize ?? OneOcr.RecognizeAsync)(image, ocrDirectory, CancellationToken.None).ConfigureAwait(false);
            string text = FileNamePattern.CleanOcr(result.Lines.FirstOrDefault() ?? "");
            if (result.Error is null && !string.IsNullOrWhiteSpace(text)) return text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { }
        return "OCRなし";
    }
}
