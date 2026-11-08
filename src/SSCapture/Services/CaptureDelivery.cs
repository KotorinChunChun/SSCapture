namespace SSCapture.Services;

internal sealed record CaptureDelivery(CaptureIntent Intent, OutputDestination Completed, OutputDestination Failed,
    bool FolderOpenFailed = false, string? SavedPath = null, Rectangle? CaptureBounds = null, string? CaptureDescription = null, bool MetadataFailed = false,
    string? NotificationPath = null, NotificationImage? NotificationImage = null, AppSettings? CaptureSettings = null, long? RetrySerial = null)
{
    public bool HasSuccess => Completed != OutputDestination.None;
    public bool HasFailure => Failed != OutputDestination.None || FolderOpenFailed || MetadataFailed;

    // 通知には対象画面と座標だけを加え、パス・OCR・例外詳細は表示しない。
    public string Message
    {
        get
        {
            List<string> lines = [];
            string? prefix = CreateSubjectPrefix();
            bool copiedAndSaved = Completed.HasFlag(OutputDestination.Clipboard | OutputDestination.File);
            if (copiedAndSaved) Append("コピー・保存しました");
            Add(OutputDestination.Clipboard, prefix is null ? "コピーが完了しました" : "コピーしました",
                "コピーできませんでした", copiedAndSaved);
            string defaultSaved = Intent == CaptureIntent.Recording ? "動画を保存しました" : "スクリーンショットを保存しました";
            Add(OutputDestination.File, string.IsNullOrEmpty(CaptureDescription) ? defaultSaved : "保存しました",
                "保存できませんでした", copiedAndSaved);
            Add(OutputDestination.ExternalApp, "外部アプリへ送りました", "外部アプリへ送れませんでした");
            Add(OutputDestination.Pinned, "ピン留めしました", "ピン留めできませんでした");
            if (FolderOpenFailed) lines.Add("保存フォルダを開けませんでした");
            if (MetadataFailed) lines.Add("画像は保存済みですが撮影情報JSONを保存できませんでした");
            return string.Join(Environment.NewLine, lines);

            void Append(string text)
            {
                lines.Add(prefix + text);
                prefix = null;
            }

            void Add(OutputDestination output, string success, string failure, bool alreadySummarized = false)
            {
                if (!alreadySummarized && Completed.HasFlag(output)) Append(success);
                if (Failed.HasFlag(output)) Append(failure);
            }
        }
    }

    private string? CreateSubjectPrefix()
    {
        if (CaptureDescription is not { Length: > 0 } description) return null;
        if (Intent == CaptureIntent.Recording)
            description += description.EndsWith('】') ? " の動画" : "の動画";
        return description + (description.EndsWith('】') ? " を" : "を");
    }
}
