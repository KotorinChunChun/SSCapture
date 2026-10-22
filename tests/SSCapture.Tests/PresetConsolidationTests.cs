using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void PresetConsolidationTests()
    {
        Run("統合画面は9分類と内部区分で旧入力・命名ページを重複表示しない", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            Equal("基本,入力プリセット,録画・音声,出力プリセット,外部アプリ,ショートカット,即時撮影キー,通知,履歴", string.Join(',', tabs.TabPages.Cast<TabPage>().Select(p => p.Text)));
            var input = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "入力プリセット"); var output = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "出力プリセット");
            Check(input.Contains(form.Controls.Find("ScreenshotPaddingPixels", true).Single()), "入力に余白がない");
            Check(output.Contains(form.Controls.Find("ScreenshotColorDepth", true).Single()), "出力に減色がない");
            Check(tabs.TabPages[0].Contains(form.Controls.Find("LimitToStartingMonitor", true).Single()), "モニタ制限が基本にない");
            Check(!((RegionManagerForm)input.Controls[0]).AutoScroll, "入力にスクロールがある");
            Check(!input.Contains(form.Controls.Find("SystemAudio", true).Single()), "録画音声が入力に混入");
        });
        Run("入力の複製・既定・削除保護と基本概要からの編集", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings(), s => { saved = s; return null; }, () => { }, () => []);
            var editor = (RegionManagerForm)form.Controls.Find("RegionManagerForm", true).Single();
            ConsolidationClick(form, "DuplicateInputPreset"); form.Controls.Find("RegionName", true).Single().Text = "ウィンドウ用";
            ((ComboBox)form.Controls.Find("CaptureMode", true).Single()).SelectedIndex = (int)CaptureMode.Window;
            ConsolidationClick(form, "DefaultInputPreset"); Equal(2, editor.Regions.Length); Equal(1, editor.Regions.Count(p => p.IsDefault));
            ConsolidationClick(form, "DeleteRegion"); Equal(2, editor.Regions.Length);
            SaveConsolidation(form); Check(saved is not null, "保存しない"); Equal(CaptureMode.Window, saved!.DefaultMode);
            ConsolidationClick(form, "DefaultInputSummary"); Equal("入力プリセット", ((TabControl)form.Controls.Find("SettingsTabs", true).Single()).SelectedTab!.Text);
            Equal("ウィンドウ用", form.Controls.Find("RegionName", true).Single().Text);
        });
        Run("出力の複製・既定は減色と命名を切り替え入力余白を維持", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings { ScreenshotPaddingPixels = 12 }, s => { saved = s; return null; }, () => { }, () => []);
            var editor = (CapturePresetEditor)form.Controls.Find("CapturePresetEditor", true).Single();
            ConsolidationClick(form, "DuplicateCapturePreset"); form.Controls.Find("CapturePresetName", true).Single().Text = "16色";
            ((ComboBox)form.Controls.Find("ScreenshotColorDepth", true).Single()).SelectedIndex = (int)ScreenshotColorDepth.Indexed4;
            form.Controls.Find("FileNamePattern", true).Single().Text = "sample.{ext}";
            ConsolidationClick(form, "DefaultCapturePreset"); ConsolidationClick(form, "DeleteCapturePreset"); Equal(2, editor.Presets.Length);
            SaveConsolidation(form); Check(saved is not null, "出力の既定が保存されない");
            Equal(ScreenshotColorDepth.Indexed4, saved!.ScreenshotColorDepth); Equal("sample.{ext}", saved.FileNamePattern); Equal(12, saved.ScreenshotPaddingPixels);
            ConsolidationClick(form, "DefaultOutputSummary"); Equal("16色", form.Controls.Find("CapturePresetName", true).Single().Text);
        });
        Run("スナップ数値と共通表示設定を保存してプリセットと独立維持", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings(), s => { saved = s; return null; }, () => { }, () => []);
            ((NumericUpDown)form.Controls.Find("SnapDistancePixels", true).Single()).Value = 5;
            ((NumericUpDown)form.Controls.Find("GridStepPixels", true).Single()).Value = 7;
            ((CheckBox)form.Controls.Find("GridEnabled", true).Single()).Checked = true;
            SaveConsolidation(form); Equal(5, saved!.SnapDistancePixels); Equal(7, saved.GridStepPixels); Check(saved.Grid20, "刻み設定が反映されない");
            saved.Validate();
        });
        Run("入力と出力の一時差分とリセットは相互に影響しない", () =>
        {
            var session = MenuSession();
            try
            {
                Check(!session.InputPresetModified && !session.OutputPresetModified, "開始直後に変更扱い");
                session.ChangePadding(10); session.ChangeColor(ScreenshotColorDepth.Indexed4);
                Check(session.InputPresetModified && session.OutputPresetModified, "差分が表示されない");
                Check(session.SelectionMenu.Command("InputPresetMenu").Text!.Contains("変更あり"), "入力の変更表示がない");
                session.ResetOutputPreset(); Equal(10, session.OutputSettings.ScreenshotPaddingPixels); Check(!session.OutputPresetModified && session.InputPresetModified, "出力復元が入力へ影響");
                session.ChangeColor(ScreenshotColorDepth.Indexed8); WaitHistory(session.ResetInputPresetAsync());
                Check(!session.InputPresetModified && session.OutputPresetModified, "入力復元が出力へ影響");
                Equal(ScreenshotColorDepth.Indexed8, session.OutputSettings.ScreenshotColorDepth);
            }
            finally { session.Cancel(); }
        });
        Run("撮影中の保存詳細は検証後だけ適用しプリセットを不変に保つ", () =>
        {
            var session = MenuSession();
            try
            {
                string original = session.OutputSettings.FileNamePattern;
                session.ChangeOutputDetails(session.OutputSettings.SaveDirectory, "test_{##}.{ext}", "-");
                Check(session.OutputPresetModified, "詳細編集を差分としない");
                try { session.ChangeOutputDetails("relative", "{unknown}", "-"); throw new Exception("無効な詳細を適用"); } catch (ArgumentException) { }
                Equal("test_{##}.{ext}", session.OutputSettings.FileNamePattern);
                session.ResetOutputPreset(); Equal(original, session.OutputSettings.FileNamePattern);
            }
            finally { session.Cancel(); }
        });
        Run("入力プリセットの画面再取得失敗は元のプリセット選択も保持", () =>
        {
            var session = MenuSession((_, _) => throw new IOException("失敗"));
            try
            {
                var preset = SavedRegion.FromSettings("別入力", session.OutputSettings with { IncludeCursor = true, FixedWidth = 100, FixedHeight = 80, DefaultMode = CaptureMode.FixedRegion });
                WaitHistory(session.ApplyInputPresetAsync(preset));
                Check(!session.OutputSettings.IncludeCursor && !session.InputPresetModified, "失敗した入力を採用");
                Check(!session.SelectionMenu.Command("InputPresetMenu").Text!.Contains("別入力"), "失敗したプリセット名を表示");
            }
            finally { session.Cancel(); }
        });
        Run("プリセット移行は初回保存時に旧原本を退避し再移行しない", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json"), old = "{\"SchemaVersion\":3,\"ScreenshotPaddingPixels\":12}";
            File.WriteAllText(path, old); var store = new SettingsStore(path); var settings = store.Load(out var warning);
            Equal<string?>(null, warning); Equal(old, File.ReadAllText(path)); store.Save(settings);
            Equal(old, File.ReadAllText(path + ".presets-v0.bak")); var loaded = store.Load(out warning); SettingsEqual(settings, loaded);
            store.Save(loaded); Equal(old, File.ReadAllText(path + ".presets-v0.bak"));
        }));
        Run("旧設定から既定を生成し入出力と固有余白を欠落なく移行", () =>
        {
            var old = new AppSettings { IncludeCursor = true, ScreenshotPaddingPixels = 12, RecordMicrophone = true,
                ScreenshotColorDepth = ScreenshotColorDepth.Indexed4, CapturePresets = [new() { Name = "資料", PaddingPixels = 50 }] };
            var migrated = old.EnsurePresets(); migrated.Validate();
            Equal(1, migrated.SavedRegions.Count(p => p.IsDefault)); Equal(1, migrated.CapturePresets.Count(p => p.IsDefault));
            Check(migrated.SavedRegions.Any(p => p.PaddingPixels == 50), "旧出力の余白を失った");
            var projected = migrated.ApplyDefaults(); Equal(12, projected.ScreenshotPaddingPixels);
            Check(projected.IncludeCursor && projected.RecordMicrophone, "移行で取得条件や録画音声を失った");
            Check(ReferenceEquals(migrated, migrated.EnsurePresets()), "重複移行した");
            var output = CapturePreset.FromSettings("出力", old with { ScreenshotColorDepth = ScreenshotColorDepth.Indexed8 });
            var result = output.Apply(projected); Equal(12, result.ScreenshotPaddingPixels); Equal(ScreenshotColorDepth.Indexed8, result.ScreenshotColorDepth);
            var input = SavedRegion.FromSettings("入力", old with { ScreenshotPaddingPixels = 20, DefaultMode = CaptureMode.Window });
            result = input.Apply(result); Equal(20, result.ScreenshotPaddingPixels); Equal(CaptureMode.Window, result.DefaultMode);
            Equal(ScreenshotColorDepth.Indexed8, result.ScreenshotColorDepth); Check(result.RecordMicrophone, "入力プリセットが録画音声を変えた");
        });
        Run("座標なしプリセットは画面構成なしで有効・既定は各1件", () =>
        {
            var settings = new AppSettings().EnsurePresets(); settings.Validate();
            settings.SavedRegions[0].Validate();
            try { (settings with { SavedRegions = [] }).Validate(); throw new Exception("既定なしを受理した"); } catch (ArgumentException) { }
            var duplicate = settings.SavedRegions[0] with { Id = "second" };
            try { (settings with { SavedRegions = [settings.SavedRegions[0], duplicate] }).Validate(); throw new Exception("複数既定を受理した"); } catch (ArgumentException) { }
        });
        Run("スナップ距離と刻み幅は20既定・独立した物理px", () =>
        {
            Equal(20, new AppSettings().SnapDistancePixels); Equal(20, new AppSettings().GridStepPixels);
            var snap = new SelectionSnap([], [new(100, 100, 100, 100)], 5);
            Equal(new Point(100, 130), snap.Apply(new(105, 130))); Equal(new Point(106, 130), snap.Apply(new(106, 130)));
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-200, -200, 600, 600)], []) { Grid20 = true };
            gesture.SetSnapPrecision(5, 7); gesture.Begin(new(-100, -100), new(1, 1)); gesture.Move(new(-83, -83));
            Equal(new Size(14, 14), gesture.Selection.Size);
            gesture.Move(new(-110, -110)); Equal(new Size(7, 7), gesture.Selection.Size);
            gesture.ExpandMonitors([new(-200, -200, 700, 700)]); Equal(5, gesture.SnapDistancePixels); Equal(7, gesture.GridStepPixels);
        });
        Run("extは静止画・録画・OCR省略・連番で重複拡張子を付けない", () =>
        {
            var at = new DateTimeOffset(2026, 10, 22, 12, 34, 56, TimeSpan.Zero);
            Equal("20261022_123456.png", FileNamePattern.Expand(FileNamePattern.Default, at, CaptureIntent.Screenshot));
            Equal("20261022_123456.mp4", FileNamePattern.Expand(FileNamePattern.Default, at, CaptureIntent.Recording));
            Equal("sample.png", FileNamePattern.Expand("sample_{ocr}.{ext}", at, CaptureIntent.Screenshot));
            Equal("sample_02.mp4", FileNamePattern.Expand("sample_{##}.{ext}", at, CaptureIntent.Recording, sequence: 2));
            Equal("legacy.mp4", FileNamePattern.Expand("legacy.png", at, CaptureIntent.Recording));
        });
        Run("スナップ精度の不正値を拒否し次の撮影へ一時値を漏らさない", () =>
        {
            foreach (int value in new[] { 0, -1, 32769 })
            {
                try { new AppSettings { GridStepPixels = value }.Validate(); throw new Exception("刻み幅を受理した"); } catch (ArgumentException) { }
                try { new SelectionSnap([], [], value); throw new Exception("吸着距離を受理した"); } catch (ArgumentException) { }
            }
            var settings = new AppSettings(); var session = MenuSession(settings: settings);
            try { session.ChangeSnapPrecision(3, 9); Equal(3, session.OutputSettings.SnapDistancePixels); Equal(20, settings.SnapDistancePixels); }
            finally { session.Cancel(); }
        });
    }
    private static void ConsolidationClick(Control form, string name) => typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form.Controls.Find(name, true).Single(), [EventArgs.Empty]);
    private static void SaveConsolidation(SettingsForm form) => typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
}
