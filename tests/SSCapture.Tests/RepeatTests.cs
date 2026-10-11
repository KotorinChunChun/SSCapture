using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    static partial void RepeatTests()
    {
        Run("登録範囲の設定・専用キーは再読込後も同じIDと座標を使う", () => WithServiceDirectory(directory =>
        {
            var region = RegionCoordinates.FromBounds("本文", new(100, 100, 640, 480), RegionLayout, shortcut: "Win+Ctrl+F8");
            var original = new AppSettings { SavedRegions = [region], IncludeCursor = true, FixedWidth = 333, FixedHeight = 222 };
            var store = new SettingsStore(Path.Combine(directory, "settings.json")); store.Save(original);
            var restored = new SettingsStore(Path.Combine(directory, "settings.json")).Load(out var warning);
            Equal<string?>(null, warning); Equal(region.Id, restored.SavedRegions.Single().Id);
            Equal(region.Bounds, RegionCoordinates.Resolve(restored.SavedRegions.Single(), RegionLayout));
            Equal(333, restored.FixedWidth); Equal(222, restored.FixedHeight); Check(restored.IncludeCursor, "カーソル設定が消失");
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(restored), KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftControl);
            Equal(region.Id, state.Process((int)Keys.F8, true, true).RegionId);
            bool rejected = false;
            try { (restored with { ScreenshotShortcut1 = "Win+Ctrl+F8" }).Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "登録範囲と通常キーの衝突を拒否しない");
        }));
        Run("前回範囲キーは通常撮影と区別しrepeatと解放を抑止", () =>
        {
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(new AppSettings()), KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftControl | KeyboardModifiers.LeftShift);
            Equal(new HotkeyDecision(true, true, CaptureIntent.Screenshot, RegionId: AppSettings.LastRegionKeyId), state.Process(0x53, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot, RegionId: AppSettings.LastRegionKeyId), state.Process(0x53, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot, RegionId: AppSettings.LastRegionKeyId), state.Process(0x53, false, false));
        });
        Run("範囲名変数は不正文字を除去し変数風の文字は再展開しない", () =>
        {
            var plan = new FileNamePlan("{region}_{##}.png", DateTimeOffset.Now, CaptureIntent.Screenshot, Region: "画面/本文 {#}");
            Equal("画面本文 {#}_01.png", plan.Candidate(0));
            Equal("画面本文 {#}_02.png", plan.Candidate(1));
            Equal("_CON", FileNamePattern.CleanRegion("CON"));
        });
        Run("履歴は入力画像から独立し上限で古い画像を解放", () =>
        {
            using var history = new CaptureHistory(2, 200);
            using var image = new Bitmap(4, 4); image.SetPixel(0, 0, Color.Red);
            history.Add(image, null); var first = history.Entries.Single();
            image.SetPixel(0, 0, Color.Blue);
            using var copy = history.CloneImage(first.Id); Equal(Color.Red.ToArgb(), copy!.GetPixel(0, 0).ToArgb());
            Equal<string?>(null, first.SavedPath);
            history.Add(image, "C:\\test.png"); history.Add(image, null);
            Equal(2, history.Entries.Count); Check(history.CloneImage(first.Id) is null, "古い履歴が残る");
            history.Clear(); Equal(0, history.Entries.Count);
        });
        Run("履歴の容量超過画像は保存せずメモリー上限を守る", () =>
        {
            using var history = new CaptureHistory(20, 100);
            using var small = new Bitmap(4, 4); using var large = new Bitmap(10, 10);
            history.Add(small, null); history.Add(small, null); Equal(1, history.Entries.Count);
            history.Add(large, null); Equal(1, history.Entries.Count);
        });
        Run("終了後の非同期出力から履歴を復活させない", () =>
        {
            var history = new CaptureHistory(); using var image = new Bitmap(2, 2);
            history.Add(image, null); history.Dispose(); history.Add(image, null);
            Equal(0, history.Entries.Count);
        });
        Run("前回範囲の新既定キーより移行前の割当を優先", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"SchemaVersion\":2,\"ScreenshotShortcut1\":\"Win+Ctrl+Shift+S\"}");
            var settings = new SettingsStore(path).Load(out var warning);
            Equal<string?>(null, warning); Equal("", settings.LastRegionShortcut); Equal("Win+Ctrl+Shift+S", settings.ScreenshotShortcut1);
        }));
    }
}
