using System.Text.Json;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SnapshotRefactorTests()
    {
        Run("再撮影設定は入出力配列・モニタ構成・即時キーを元の設定や返却値と共有しない", () =>
        {
            var source = SnapshotFixtureSettings();
            var recipe = LastCaptureRecipe.Create(1, new(1, 1, 2, 2), null, source, RegionLayout);
            string expected = JsonSerializer.Serialize(recipe.Settings);
            var returned = recipe.Settings;
            ChangeSnapshotArrays(source);
            Equal(expected, JsonSerializer.Serialize(recipe.Settings));
            ChangeSnapshotArrays(returned);
            Equal(expected, JsonSerializer.Serialize(recipe.Settings));
        });

        Run("再撮影は運用設定8項目だけ現在値を使い確定時の入出力条件を維持する", () =>
        {
            var captured = SnapshotFixtureSettings() with { ScreenshotPaddingPixels = 17, ScreenshotColorDepth = ScreenshotColorDepth.Indexed4,
                IncludeCursor = true, FixedWidth = 333, FixedHeight = 222, RecordSystemAudio = true };
            var current = new AppSettings { KeepHistory = false, OneOcrDirectory = "C:\\current-ocr", NotificationDurationSeconds = 25,
                PlayCaptureSound = false, NotificationApplicationId = "current-app", NotificationCorner = NotificationCorner.Center,
                NotificationDisplay = NotificationDisplay.Display2, HoldNotificationWhileApproaching = false,
                DefaultMode = CaptureMode.Window, DefaultDestination = OutputDestination.File, SaveDirectory = "C:\\current-save",
                FileNamePattern = "現在_{##}.{ext}", ScreenshotShortcut1 = "Ctrl+F9", FreezeFrame = false, ScreenshotPaddingPixels = 0,
                ScreenshotColorDepth = ScreenshotColorDepth.Original, FixedWidth = 900, FixedHeight = 700, Grid20 = true };
            var expected = captured with { KeepHistory = false, OneOcrDirectory = "C:\\current-ocr", NotificationDurationSeconds = 25,
                PlayCaptureSound = false, NotificationApplicationId = "current-app", NotificationCorner = NotificationCorner.Center,
                NotificationDisplay = NotificationDisplay.Display2, HoldNotificationWhileApproaching = false };
            var recipe = LastCaptureRecipe.Create(1, new(1, 1, 2, 2), null, captured, RegionLayout);
            string capturedBefore = JsonSerializer.Serialize(captured), currentBefore = JsonSerializer.Serialize(current);
            var replay = recipe.SettingsForReplay(current);
            Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(replay));
            Equal(capturedBefore, JsonSerializer.Serialize(captured));
            Equal(currentBefore, JsonSerializer.Serialize(current));
            ChangeSnapshotArrays(replay);
            Equal(capturedBefore, JsonSerializer.Serialize(recipe.Settings));
        });

        Run("設定の独立コピーはJSONへ保存する値・ID・版・登録順序を変えない", () =>
        {
            var source = SnapshotFixtureSettings();
            var copy = CaptureSettingsSnapshot.Copy(source);
            Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(copy));
            Check(!ReferenceEquals(source.SavedRegions, copy.SavedRegions) && !ReferenceEquals(source.SavedRegions[0].Layout, copy.SavedRegions[0].Layout), "入力配列が共有");
            Check(!ReferenceEquals(source.CapturePresets, copy.CapturePresets) && !ReferenceEquals(source.ImageEditors, copy.ImageEditors)
                && !ReferenceEquals(source.VideoEditors, copy.VideoEditors) && !ReferenceEquals(source.InstantShortcuts, copy.InstantShortcuts), "設定配列が共有");
        });

        Run("再撮影の記録簿は並行した成功で最大世代を保持し新しい無効化後に旧値を戻さない", () =>
        {
            var book = new CaptureRecipeBook();
            Enumerable.Range(1, 128).AsParallel().ForAll(sequence => book.Remember(sequence,
                LastCaptureRecipe.Create(sequence, new(1, 1, 2, 2), null, new AppSettings(), RegionLayout)));
            Equal(128L, book.Current!.Sequence);
            book.Remember(129, null);
            book.Remember(128, LastCaptureRecipe.Create(128, new(1, 1, 2, 2), null, new(), RegionLayout));
            Check(book.Current is null, "無効化後に旧レシピが復活");
        });
    }

    private static AppSettings SnapshotFixtureSettings() => new()
    {
        SavedRegions = [RegionCoordinates.FromBounds("入力", new(1, 1, 2, 2), RegionLayout)],
        CapturePresets = [new CapturePreset { Name = "出力" }],
        ImageEditors = [new ExternalApplication { Id = "image", Name = "画像", Executable = "C:\\image.exe" }],
        VideoEditors = [new ExternalApplication { Id = "video", Name = "動画", Executable = "C:\\video.exe" }],
        SelectedImageEditorId = "image", SelectedVideoEditorId = "video"
    };

    private static void ChangeSnapshotArrays(AppSettings value)
    {
        value.SavedRegions[0].Layout[0] = value.SavedRegions[0].Layout[0] with { Name = "変更された画面" };
        value.SavedRegions[0] = value.SavedRegions[0] with { Name = "変更された入力" };
        value.CapturePresets[0] = value.CapturePresets[0] with { Name = "変更された出力" };
        value.ImageEditors[0] = value.ImageEditors[0] with { Name = "変更された画像アプリ" };
        value.VideoEditors[0] = value.VideoEditors[0] with { Name = "変更された動画アプリ" };
        value.InstantShortcuts[0] = "Ctrl+F8";
    }
}
