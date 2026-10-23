namespace SSCapture.Services;

// 通知の内容だけを決定する。表示、ファイル生成、起動、画像の所有権は扱わない。
internal sealed record NotificationPresentation(string Message, bool HasFailure,
    string? SavedFile, ExternalApplication? Editor, string RepeatHint)
{
    internal static NotificationPresentation Create(CaptureDelivery result, AppSettings current, bool hasPendingImage)
    {
        string? savedFile = result.Completed.HasFlag(OutputDestination.File) ? result.SavedPath : null;
        var app = ResolveApplication(result, current);
        bool canEdit = result.HasSuccess && app is not null
            && (result.SavedPath is not null || result.NotificationPath is not null || hasPendingImage);

        string key = string.IsNullOrWhiteSpace(current.LastRecipeShortcut) ? "未設定" : current.LastRecipeShortcut;
        string repeatHint = result.Intent == CaptureIntent.Recording
            ? $"静止画の直前設定で再撮影: {key}（録画の再開ではありません）"
            : $"直前の設定で再撮影: {key}";

        return new(result.Message, result.HasFailure, savedFile, canEdit ? app : null, repeatHint);
    }

    internal static ExternalApplication? ResolveApplication(CaptureDelivery result, AppSettings current)
    {
        var registered = current.ImageEditors.Concat(current.VideoEditors).FirstOrDefault(app => app.Id == current.NotificationApplicationId);
        if (registered is not null) return registered;

        var captured = result.CaptureSettings;
        if (captured is null) return null;
        bool video = result.Intent == CaptureIntent.Recording;
        string id = video ? captured.SelectedVideoEditorId : captured.SelectedImageEditorId;
        var original = (video ? captured.VideoEditors : captured.ImageEditors).FirstOrDefault(app => app.Id == id);
        var available = (video ? current.VideoEditors : current.ImageEditors).FirstOrDefault(app => app.Id == id);
        return original is not null && original == available ? available : null;
    }
}
