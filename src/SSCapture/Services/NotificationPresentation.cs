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
            && (result.SavedPath is not null || result.NotificationPath is not null || result.PrepareNotificationFile is not null || hasPendingImage);

        string key = current.LastRecipeShortcut;
        string repeatHint = string.IsNullOrWhiteSpace(key) ? "" : result.Intent == CaptureIntent.Recording
            ? $"直前の静止画設定で再撮影するキー: {key}"
            : $"直前の設定で再撮影するキー: {key}";

        return new(result.Message, result.HasFailure, savedFile, canEdit ? app : null, repeatHint);
    }

    internal static ExternalApplication? ResolveApplication(CaptureDelivery result, AppSettings current)
    {
        var applications = EditorsFor(result.Intent, current);
        var registered = applications.FirstOrDefault(app => app.Id == current.NotificationApplicationId);
        if (registered is not null) return registered;

        var captured = result.CaptureSettings;
        if (captured is null) return null;
        bool video = result.Intent == CaptureIntent.Recording;
        string id = video ? captured.SelectedVideoEditorId : captured.SelectedImageEditorId;
        var original = (video ? captured.VideoEditors : captured.ImageEditors).FirstOrDefault(app => app.Id == id);
        var available = applications.FirstOrDefault(app => app.Id == id);
        return original is not null && original == available ? available : null;
    }

    private static IEnumerable<ExternalApplication> EditorsFor(CaptureIntent intent, AppSettings settings) =>
        intent == CaptureIntent.Screenshot ? settings.ImageEditors :
            settings.VideoEditors.Where(app => app.Launch == ExternalApplicationLaunch.Executable);

    internal static ExternalApplication[] ScreenshotEditors(AppSettings settings, ExternalEditor editor) =>
        EditorsFor(CaptureIntent.Screenshot, settings).Where(editor.CanOpen).ToArray();
}
