namespace SSCapture.Services;

internal sealed record CaptureDelivery(CaptureIntent Intent, OutputDestination Completed, OutputDestination Failed,
    bool FolderOpenFailed = false, string? SavedPath = null, Rectangle? CaptureBounds = null, string? CaptureDescription = null)
{
    public bool HasSuccess => Completed != OutputDestination.None;
    public bool HasFailure => Failed != OutputDestination.None || FolderOpenFailed;

    // 通知には対象画面と座標だけを加え、パス・OCR・例外詳細は表示しない。
    public string Message
    {
        get
        {
            List<string> lines = [];
            string? subject = CaptureDescription is { Length: > 0 } description
                ? description + (Intent == CaptureIntent.Recording ? (description.EndsWith('】') ? " の動画" : "の動画") : "") : null;
            string? prefix = subject is null ? null : subject + (subject.EndsWith('】') ? " を" : "を");
            Add(OutputDestination.Clipboard, prefix is null ? "コピーが完了しました" : prefix + "コピーしました",
                prefix is null ? "コピーできませんでした" : prefix + "コピーできませんでした");
            Add(OutputDestination.File, prefix is null ? Intent == CaptureIntent.Recording ? "動画を保存しました" : "スクリーンショットを保存しました" : prefix + "保存しました",
                prefix is null ? "保存できませんでした" : prefix + "保存できませんでした");
            Add(OutputDestination.ExternalApp, prefix is null ? "外部アプリへ送りました" : prefix + "外部アプリへ送りました",
                prefix is null ? "外部アプリへ送れませんでした" : prefix + "外部アプリへ送れませんでした");
            if (FolderOpenFailed) lines.Add("保存フォルダを開けませんでした");
            return string.Join(Environment.NewLine, lines);

            void Add(OutputDestination output, string success, string failure)
            {
                if (Completed.HasFlag(output)) lines.Add(success);
                if (Failed.HasFlag(output)) lines.Add(failure);
            }
        }
    }
}
