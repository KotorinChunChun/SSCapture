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
                ? description + (Intent == CaptureIntent.Recording ? "の動画" : "") : null;
            Add(OutputDestination.Clipboard, subject is null ? "コピーが完了しました" : subject + "をコピーしました。",
                subject is null ? "コピーできませんでした" : subject + "をコピーできませんでした。");
            Add(OutputDestination.File, subject is null ? Intent == CaptureIntent.Recording ? "動画を保存しました" : "スクリーンショットを保存しました" : subject + "を保存しました。",
                subject is null ? "保存できませんでした" : subject + "を保存できませんでした。");
            Add(OutputDestination.ExternalApp, subject is null ? "外部アプリへ送りました" : subject + "を外部アプリへ送りました。",
                subject is null ? "外部アプリへ送れませんでした" : subject + "を外部アプリへ送れませんでした。");
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
