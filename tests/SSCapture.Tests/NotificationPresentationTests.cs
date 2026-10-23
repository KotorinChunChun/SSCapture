using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void NotificationPresentationTests()
    {
        Run("通知判断の文言と失敗状態は元結果と等価で旧クリック設定に依存しない", () =>
        {
            var settings = SnapshotFixtureSettings() with { NotificationApplicationId = "image" };
            foreach (CaptureIntent intent in Enum.GetValues<CaptureIntent>())
            foreach (int mask in Enumerable.Range(0, 8))
            {
                var result = new CaptureDelivery(intent, (OutputDestination)mask, (OutputDestination)(7 ^ mask), FolderOpenFailed: true,
                    SavedPath: "C:\\非表示の保存先\\画像.png", CaptureBounds: new(-1200, -100, 100, 50), MetadataFailed: true, CaptureSettings: settings);
                var expected = NotificationPresentation.Create(result, settings, false);
                Equal(result.Message, expected.Message); Equal(result.HasFailure, expected.HasFailure);
                Check(!expected.Message.Contains("非表示の保存先"), "通知へ保存パスが混入");
                foreach (var oldAction in Enum.GetValues<NotificationClickAction>())
                    Equal(expected, NotificationPresentation.Create(result, settings with { NotificationClickAction = oldAction }, false));
            }
        });

        Run("通知ボタンは成功フラグ・保存パス・未保存画像の組合せだけで可否が決まる", () =>
        {
            var settings = SnapshotFixtureSettings() with { NotificationApplicationId = "image" };
            var cases = new[]
            {
                (OutputDestination.File, OutputDestination.None, "saved.png", (string?)null, false, true, true),
                (OutputDestination.Clipboard, OutputDestination.None, (string?)null, (string?)null, true, false, true),
                (OutputDestination.Clipboard, OutputDestination.None, (string?)null, (string?)null, false, false, false),
                (OutputDestination.None, OutputDestination.File, (string?)null, "temporary.png", true, false, false),
                (OutputDestination.Clipboard, OutputDestination.File, (string?)null, "temporary.png", false, false, true),
                (OutputDestination.File, OutputDestination.None, (string?)null, (string?)null, false, false, false),
                (OutputDestination.Clipboard, OutputDestination.None, "saved.png", (string?)null, false, false, true)
            };
            foreach (var (completed, failed, saved, temporary, hasImage, folderEnabled, editorEnabled) in cases)
            {
                var result = new CaptureDelivery(CaptureIntent.Screenshot, completed, failed, SavedPath: saved, NotificationPath: temporary);
                var actual = NotificationPresentation.Create(result, settings, hasImage);
                Equal(folderEnabled, actual.SavedFile is not null); Equal(editorEnabled, actual.Editor is not null);
                if (folderEnabled) Equal(saved, actual.SavedFile);
            }
        });

        Run("通知専用アプリは撮影時アプリより優先し画像動画の区別を強制しない", () =>
        {
            var captured = SnapshotFixtureSettings();
            var current = captured with { NotificationApplicationId = "image" };
            foreach (CaptureIntent intent in Enum.GetValues<CaptureIntent>())
            {
                var result = new CaptureDelivery(intent, OutputDestination.Clipboard, OutputDestination.None, CaptureSettings: captured);
                Equal(current.ImageEditors[0], NotificationPresentation.Create(result, current, true).Editor);
                Equal(current.ImageEditors[0], NotificationPresentation.ResolveApplication(result with { CaptureSettings = null }, current));
            }
        });

        Run("通知の撮影時アプリへの代替は種別を守り削除・変更時に無効になる", () =>
        {
            var captured = SnapshotFixtureSettings();
            var shot = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None, CaptureSettings: captured);
            var movie = shot with { Intent = CaptureIntent.Recording };
            Equal(captured.ImageEditors[0], NotificationPresentation.ResolveApplication(shot, captured));
            Equal(captured.VideoEditors[0], NotificationPresentation.ResolveApplication(movie, captured));
            Equal<ExternalApplication?>(null, NotificationPresentation.ResolveApplication(shot, captured with { ImageEditors = [] }));
            Equal<ExternalApplication?>(null, NotificationPresentation.ResolveApplication(movie, captured with { VideoEditors = [] }));
            Equal<ExternalApplication?>(null, NotificationPresentation.ResolveApplication(shot, captured with { ImageEditors = [captured.ImageEditors[0] with { Arguments = "--changed {file}" }] }));
            Equal<ExternalApplication?>(null, NotificationPresentation.ResolveApplication(movie, captured with { VideoEditors = [captured.VideoEditors[0] with { Name = "変更済み" }] }));
            Equal<ExternalApplication?>(null, NotificationPresentation.ResolveApplication(shot with { CaptureSettings = null }, captured));
            Equal(captured.ImageEditors[0], NotificationPresentation.ResolveApplication(shot, captured with { NotificationApplicationId = "見つからない登録" }));
        });

        Run("通知の再撮影案内は実キーか未設定を表示し録画再開とは区別する", () =>
        {
            foreach (string key in new[] { "", " \t ", "Ctrl+F11" })
            {
                var settings = new AppSettings { LastRecipeShortcut = key };
                string expectedKey = string.IsNullOrWhiteSpace(key) ? "未設定" : key;
                var shot = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.Clipboard, OutputDestination.None);
                Equal($"直前の設定で再撮影: {expectedKey}", NotificationPresentation.Create(shot, settings, true).RepeatHint);
                Equal($"静止画の直前設定で再撮影: {expectedKey}（録画の再開ではありません）",
                    NotificationPresentation.Create(shot with { Intent = CaptureIntent.Recording }, settings, true).RepeatHint);
            }
        });

        Run("通知判断はファイル検証・画像生成・外部アプリ起動を行わない", () => WithServiceDirectory(directory =>
        {
            var app = new ExternalApplication { Id = "unresolved", Name = "未検証", Executable = Path.Combine(directory, "never-launch.exe") };
            var current = new AppSettings { ImageEditors = [app], NotificationApplicationId = app.Id };
            var result = new CaptureDelivery(CaptureIntent.Screenshot, OutputDestination.File, OutputDestination.None, SavedPath: Path.Combine(directory, "not-written.png"));
            var actual = NotificationPresentation.Create(result, current, false);
            Equal(app, actual.Editor); Equal(result.SavedPath, actual.SavedFile);
            Equal(0, Directory.GetFiles(directory).Length);
        }));
    }
}
