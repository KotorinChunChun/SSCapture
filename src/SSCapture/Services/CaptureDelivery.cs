namespace SSCapture.Services;

internal sealed record CaptureDelivery(CaptureIntent Intent, OutputDestination Completed, OutputDestination Failed,
    bool FolderOpenFailed = false, string? SavedPath = null, Rectangle? CaptureBounds = null, string? CaptureDescription = null, bool MetadataFailed = false)
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
            Add(OutputDestination.Clipboard, prefix is null ? "コピーが完了しました" : prefix + "コピーしました",
                (prefix ?? "") + "コピーできませんでした");
            string defaultSaved = Intent == CaptureIntent.Recording ? "動画を保存しました" : "スクリーンショットを保存しました";
            Add(OutputDestination.File, prefix is null ? defaultSaved : prefix + "保存しました",
                (prefix ?? "") + "保存できませんでした");
            Add(OutputDestination.ExternalApp, (prefix ?? "") + "外部アプリへ送りました",
                (prefix ?? "") + "外部アプリへ送れませんでした");
            if (FolderOpenFailed) lines.Add("保存フォルダを開けませんでした");
            if (MetadataFailed) lines.Add("画像は保存済みですが撮影情報JSONを保存できませんでした");
            return string.Join(Environment.NewLine, lines);

            void Add(OutputDestination output, string success, string failure)
            {
                if (Completed.HasFlag(output)) lines.Add(success);
                if (Failed.HasFlag(output)) lines.Add(failure);
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
