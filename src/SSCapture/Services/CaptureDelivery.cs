namespace SSCapture.Services;

internal sealed record CaptureDelivery(CaptureIntent Intent, OutputDestination Completed, OutputDestination Failed,
    bool FolderOpenFailed = false, string? SavedPath = null, Rectangle? CaptureBounds = null)
{
    public bool HasSuccess => Completed != OutputDestination.None;
    public bool HasFailure => Failed != OutputDestination.None || FolderOpenFailed;

    // Display text deliberately excludes exception text, paths, OCR text and bounds.
    public string Message
    {
        get
        {
            List<string> lines = [];
            Add(OutputDestination.Clipboard, "コピーが完了しました", "コピーできませんでした");
            Add(OutputDestination.File, Intent == CaptureIntent.Recording ? "動画を保存しました" : "スクリーンショットを保存しました", "保存できませんでした");
            Add(OutputDestination.ExternalApp, "外部アプリへ送りました", "外部アプリへ送れませんでした");
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
