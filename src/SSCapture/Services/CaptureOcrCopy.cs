namespace SSCapture.Services;

internal static class CaptureOcrCopy
{
    internal static async Task<string> CopyAsync(Bitmap image, string directory, bool asTable, CancellationToken token,
        Func<Bitmap, string, CancellationToken, bool, Task<OcrCopyResult>>? recognize = null,
        Func<string, CancellationToken, Task>? write = null)
    {
        token.ThrowIfCancellationRequested();
        var result = await (recognize ?? OcrCopyService.RecognizeAsync)(image, directory, token, asTable);
        token.ThrowIfCancellationRequested();
        if (result.Error is { } error) throw new InvalidOperationException(error);
        if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("文字を認識できませんでした。");
        string text = asTable
            ? result.Table.Tsv ?? throw new InvalidOperationException("表の行と列を判定できませんでした。「文字をコピー」を使ってください。")
            : result.Text;
        await (write ?? ClipboardWriter.CopyTextAsync)(text, token);
        return asTable ? "表をコピーしました。" : "文字をコピーしました。";
    }
}
