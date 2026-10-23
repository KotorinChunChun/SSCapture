namespace SSCapture.Services;

internal static class CaptureSettingsSnapshot
{
    // 各recordの値は不変だが、設定が公開する配列とモニタ構成配列は共有しない。
    internal static AppSettings Copy(AppSettings settings) => settings with
    {
        SavedRegions = settings.SavedRegions.Select(region => region with { Layout = region.Layout.ToArray() }).ToArray(),
        CapturePresets = settings.CapturePresets.ToArray(),
        ImageEditors = settings.ImageEditors.ToArray(),
        VideoEditors = settings.VideoEditors.ToArray(),
        InstantShortcuts = settings.InstantShortcuts.ToArray()
    };

    // 撮影の入出力条件は当時のまま。履歴・通知等の運用設定だけは現在値を使う。
    internal static AppSettings ForReplay(AppSettings captured, AppSettings current) => Copy(captured) with
    {
        KeepHistory = current.KeepHistory,
        OneOcrDirectory = current.OneOcrDirectory,
        NotificationDurationSeconds = current.NotificationDurationSeconds,
        PlayCaptureSound = current.PlayCaptureSound,
        NotificationApplicationId = current.NotificationApplicationId,
        NotificationCorner = current.NotificationCorner,
        NotificationDisplay = current.NotificationDisplay,
        HoldNotificationWhileApproaching = current.HoldNotificationWhileApproaching
    };
}
