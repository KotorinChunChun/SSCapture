using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SettingsPageResetTests()
    {
        Run("基本と履歴の初期化は各ページの下書きだけを変更し保存で確定する", () =>
        {
            var initial = new AppSettings { GridStepPixels = 27, ShowMagnifier = true, KeepHistory = false,
                NotificationDurationSeconds = 17, SelectionGuideDisplay = SelectionGuideDisplay.Main };
            var saved = new List<AppSettings>();
            using var form = new SettingsForm(initial, next => { saved.Add(next); return null; }, () => { }, () => []);
            _ = form.Handle;
            var pages = Field<TabControl>(form, "SettingsTabs").TabPages.Cast<TabPage>()
                .Where(page => page.Text != "ショートカット").Concat(Field<TabControl>(form, "ShortcutGroups").TabPages.Cast<TabPage>());
            foreach (TabPage page in pages)
            {
                var reset = page.Controls.Find("ResetSettingsPage", true).Single();
                Equal("初期設定に戻す", reset.Text);
                Check(reset.Parent?.Parent == page && ((Button)reset).CausesValidation == false, "初期化が画面内の専用ボタンになっていない");
            }
            CaptureCommandClick(Field<TabControl>(form, "SettingsTabs").TabPages[0], "ResetSettingsPage");
            Equal(0, saved.Count);
            Equal(20m, Field<NumericUpDown>(form, "GridStepPixels").Value);
            Equal(0, Field<ComboBox>(form, "SelectionGuideDisplay").SelectedIndex);
            Check(!Field<CheckBox>(form, "ShowMagnifier").Checked, "拡大鏡が初期値に戻っていない");
            Check(!Field<CheckBox>(form, "KeepHistory").Checked, "履歴の設定が変わった");
            Equal(17m, Field<NumericUpDown>(form, "NotificationDurationSeconds").Value);

            form.SelectCategory("履歴");
            form.ResetSelectedPage();
            Equal(0, saved.Count);
            Check(Field<CheckBox>(form, "KeepHistory").Checked, "履歴の設定が初期値に戻っていない");
            SaveConsolidation(form);
            Check(saved.Last() is { GridStepPixels: 20, KeepHistory: true, NotificationDurationSeconds: 17 }, "下書きの保存に失敗");
        });

        Run("プリセット初期化はキャンセルと保存失敗で保持し成功時だけ一覧へ反映する", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var standardInput = defaults.SavedRegions.Single(region => region.IsDefault);
            var custom = SavedRegion.FromSettings("自作", defaults);
            var initial = defaults with { SavedRegions = [standardInput with { Name = "編集中" }, custom] };
            var saved = new List<AppSettings>();
            bool reject = true;
            using var form = new SettingsForm(initial, next =>
            {
                saved.Add(next);
                return reject ? "検証用の保存エラー" : null;
            }, () => { }, () => []);
            _ = form.Handle;
            form.SelectCategory("入力プリセット");
            var editor = FindRegionEditor(form);

            form.ResetSelectedPage(() => false);
            Equal(0, saved.Count);
            Equal("編集中", editor.Regions.Single(region => region.Id == standardInput.Id).Name);

            form.ResetSelectedPage(() => true);
            Equal(1, saved.Count);
            Equal("編集中", editor.Regions.Single(region => region.Id == standardInput.Id).Name);
            Check(Field<TextBox>(form, "Status").Text.Contains("検証用の保存エラー", StringComparison.Ordinal), "保存失敗が表示されない");

            reject = false;
            form.ResetSelectedPage(() => true);
            Equal(2, saved.Count);
            var restored = editor.Regions;
            Equal(standardInput.Name, restored.Single(region => region.Id == standardInput.Id).Name);
            Equal(custom.Name, restored.Single(region => region.Id == custom.Id).Name);
        });

        Run("PC作業中の初期割り当てとの競合を表示し即時撮影キーを変えない", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var instant = defaults.InstantShortcuts.ToArray();
            instant[0] = "Win+Shift+S";
            var currentRows = new PresetCaptureShortcut[] { new("Ctrl+F11") };
            using var form = new SettingsForm(defaults with { InstantShortcuts = instant, PresetCaptureShortcuts = currentRows,
                ScreenshotShortcut1 = "", ScreenshotShortcut2 = "", RecordingShortcut1 = "", RecordingShortcut2 = "", LastRecipeShortcut = "Ctrl+F12" },
                _ => throw new InvalidOperationException("ショートカットを自動保存した"), () => { }, () => []);
            _ = form.Handle;
            form.SelectCategory("ショートカット");
            var tabs = Field<TabControl>(form, "ShortcutGroups");
            _ = tabs.Handle;
            tabs.SelectedIndex = 0;
            var editor = tabs.TabPages[0].Controls.Find("PresetCaptureShortcuts", true).Single();
            form.ResetSelectedPage();
            var status = Field<TextBox>(form, "Status").Text;
            Check(status.Contains("重複", StringComparison.Ordinal) && status.Contains("即時撮影 1", StringComparison.Ordinal), "競合する割り当て名がない: " + status);
            Equal(1, ((DataGridView)editor).Rows.Count);
            Equal("Ctrl+F12", Field<HotkeyEditor>(form, "LastRecipeShortcut").Value);
            tabs.SelectedIndex = 1;
            CaptureCommandClick(tabs.TabPages[1], "ResetSettingsPage");
            Equal("Ctrl+F12", Field<HotkeyEditor>(form, "LastRecipeShortcut").Value);
            tabs.SelectedIndex = 0;
            form.ResetSelectedPage();
            Equal(3, ((DataGridView)editor).Rows.Count);
            Equal("", Field<HotkeyEditor>(form, "LastRecipeShortcut").Value);
        });

        static T Field<T>(SettingsForm form, string name) where T : Control => (T)form.Controls.Find(name, true).Single();

        static RegionManagerForm FindRegionEditor(SettingsForm form) => Field<TabControl>(form, "SettingsTabs")
            .TabPages.Cast<TabPage>().Single(page => page.Text == "入力プリセット").Controls.OfType<RegionManagerForm>().Single();
    }
}
