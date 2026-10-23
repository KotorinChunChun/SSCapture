using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CommitPresetField(Control control) => typeof(Control).GetMethod("OnValidated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(control, [EventArgs.Empty]);
    private static void PresetUpgradeTests()
    {
        Run("v2移行は編集済みの名前と値・既定・キーを保持し旧音声だけ補完", () =>
        {
            var old = new AppSettings().EnsurePresets();
            var edited = old.SavedRegions[0] with { Id = "standard-input", Name = "標準", PaddingPixels = 23, ProfileVersion = 1, Shortcut = "Ctrl+F9" };
            var migrated = (old with { PresetSchemaVersion = 2, SavedRegions = [edited], RecordSystemAudio = true, RecordMicrophone = true, MicrophoneDeviceId = "missing-device" }).EnsurePresets();
            var preserved = migrated.SavedRegions.Single(p => p.Id == edited.Id);
            Equal("標準", preserved.Name); Equal(23, preserved.PaddingPixels); Equal(edited.Shortcut, preserved.Shortcut);
            Check(preserved.IsDefault && preserved.RecordSystemAudio && preserved.RecordMicrophone, "既定または旧音声を失った");
            Equal("missing-device", preserved.MicrophoneDeviceId);
            Check(migrated.SavedRegions.Where(p => p.Id != edited.Id).All(p => !p.RecordSystemAudio && !p.RecordMicrophone), "新規候補の音声を有効にした");
            var applied = preserved.Apply(new AppSettings()); Check(applied.RecordSystemAudio && applied.RecordMicrophone, "音声を適用しない");
        });
        Run("工場出荷の固定サイズ候補は一度だけ追加し削除後に復活しない", () =>
        {
            var settings = new AppSettings().ApplyDefaults();
            Equal(5, settings.SavedRegions.Length); Check(settings.SavedRegions.All(p => p.ProfileVersion == 2), "音声形式未移行");
            var removed = settings with { SavedRegions = [settings.SavedRegions.Single(p => p.IsDefault)] };
            Equal(1, removed.EnsurePresets().SavedRegions.Length);
            Equal("囲った範囲", settings.SavedRegions.Single(p => p.IsDefault).Name);
            Equal("クリップボード", settings.CapturePresets.Single(p => p.IsDefault).Name);
        });
        Run("プリセットの自動保存は基本の下書きを確定せず後の保存でも巻き戻さない", () =>
        {
            var writes = new List<AppSettings>();
            using var form = new SettingsForm(new AppSettings(), value => { writes.Add(value); return null; }, () => { }, () => []);
            ((CheckBox)form.Controls.Find("DimCaptureBackground", true).Single()).Checked = false;
            ((CheckBox)form.Controls.Find("FreezeFrame", true).Single()).Checked = false;
            Check(writes.Count > 0 && writes[^1].DimCaptureBackground && !writes[^1].FreezeFrame, "基本の下書きまで自動保存した");
            ((ComboBox)form.Controls.Find("ScreenshotColorDepth", true).Single()).SelectedIndex = (int)ScreenshotColorDepth.Indexed4;
            Check(!writes[^1].FreezeFrame && writes[^1].DimCaptureBackground, "出力保存で入力を巻き戻した");
            SaveConsolidation(form);
            Check(!writes[^1].DimCaptureBackground && !writes[^1].FreezeFrame && writes[^1].ScreenshotColorDepth == ScreenshotColorDepth.Indexed4, "全体保存でプリセットを巻き戻した");
        });
        Run("名前の入力途中は未保存・確定値だけ保存し不正値で選択を移さない", () =>
        {
            var writes = new List<AppSettings>();
            using var form = new SettingsForm(new AppSettings(), value => { writes.Add(value); return null; }, () => { }, () => []);
            var name = form.Controls.Find("RegionName", true).Single(); name.Text = "確定後の名前"; Equal(0, writes.Count);
            CommitPresetField(name); Equal("確定後の名前", writes[^1].SavedRegions.Single(p => p.IsDefault).Name);
            int count = writes.Count; name.Text = "";
            var list = (ListBox)form.Controls.Find("SavedRegions", true).Single(); list.SelectedIndex = 1;
            Equal(0, list.SelectedIndex); Equal("", name.Text); Equal(count, writes.Count);
        });
        Run("自動保存失敗は旧一覧とディスク相当を保持し下書きを消さない", () =>
        {
            AppSettings? stored = null; bool fail = true;
            using var form = new SettingsForm(new AppSettings(), value => { if (fail) return "保存失敗の注入"; stored = value; return null; }, () => { }, () => []);
            var name = form.Controls.Find("RegionName", true).Single(); name.Text = "失敗時の下書き"; CommitPresetField(name);
            Check(stored is null && name.Text == "失敗時の下書き", "失敗時に保存または下書き消去");
            Check(((RegionManagerForm)form.Controls.Find("RegionManagerForm", true).Single()).Regions.All(p => p.Name != name.Text), "失敗した値を一覧に採用");
            fail = false; CommitPresetField(name); Equal("失敗時の下書き", stored!.SavedRegions.Single(p => p.IsDefault).Name);
        });
        Run("録画音声は入力ごとに切替し未接続マイクも保持する", () =>
        {
            AppSettings? stored = null;
            using var form = new SettingsForm(new AppSettings { MicrophoneDeviceId = "missing" }, value => { stored = value; return null; }, () => { }, () => []);
            ((CheckBox)form.Controls.Find("SystemAudio", true).Single()).Checked = true;
            Check(stored!.SavedRegions.Single(p => p.IsDefault).RecordSystemAudio, "入力にPC音声を保存しない");
            Equal("missing", stored.SavedRegions.Single(p => p.IsDefault).MicrophoneDeviceId);
            ((ListBox)form.Controls.Find("SavedRegions", true).Single()).SelectedIndex = 1;
            Check(!((CheckBox)form.Controls.Find("SystemAudio", true).Single()).Checked, "別の入力へ音声が漏れた");
        });
        Run("入出力の可視アクションは追加・削除・既定だけでプリセットページに全体保存を出さない", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            foreach (string category in new[] { "入力プリセット", "出力プリセット" })
            {
                form.SelectCategory(category);
                Check(!form.Controls.Find("SaveSettings", true).Single().Visible && form.AcceptButton is null, "全体保存がプリセットに残る");
            }
            Check(form.Controls.Find("SaveRegion", true).Length == 0 && form.Controls.Find("StoreCapturePreset", true).Length == 0, "旧保存ボタンが残る");
            foreach (string name in new[] { "AddInputPreset", "DeleteRegion", "DefaultInputPreset", "AddCapturePreset", "DeleteCapturePreset", "DefaultCapturePreset" })
                Equal(1, form.Controls.Find(name, true).Length);
        });
        Run("固定位置とサイズはマウス・矢印・Space・基点で移動しない", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.FixedRegion, [new(-1000, -1000, 2000, 2000)], [], new Size(120, 80));
            var bounds = new Rectangle(-600, -300, 120, 80); gesture.PlaceFixed(bounds, new Point(-200, -200)); gesture.FixedPosition = bounds;
            gesture.Move(new Point(600, 500), true); gesture.Nudge(Keys.Right); gesture.Space(); gesture.ChangeAnchor(CaptureAnchor.BottomRight);
            gesture.Begin(new Point(600, 500), new Size(1, 1)); Equal(bounds, gesture.End(new Point(700, 700), OutputDestination.Clipboard)!.Bounds);
        });
        Run("v2移行は読込では原本を変えず初回保存で原本退避", () => WithServiceDirectory(directory =>
        {
            var settings = new AppSettings().ApplyDefaults() with { PresetSchemaVersion = 2 };
            string path = Path.Combine(directory, "settings.json"), json = System.Text.Json.JsonSerializer.Serialize(settings); File.WriteAllText(path, json);
            var store = new SettingsStore(path); var next = store.Load(out var warning); Equal<string?>(null, warning); Equal(json, File.ReadAllText(path));
            store.Save(next); Equal(json, File.ReadAllText(path + ".presets-v2.bak"));
        }));
    }
}
