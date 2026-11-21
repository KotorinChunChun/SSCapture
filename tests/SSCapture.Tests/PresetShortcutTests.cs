using System.Text.Json;
using System.Text.Json.Nodes;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void PresetShortcutTests()
    {
        Run("v0.1.10のキーはIDを保持して各1行に移り、空を保存すると復活しない", () => WithServiceDirectory(directory =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var interactive = defaults.SavedRegions.First(p => p.Mode == CaptureMode.FixedRegion) with { Shortcut = "control + f6" };
            var fixedInput = RegionCoordinates.FromBounds("登録位置", new(-1800, 0, 120, 80), RegionLayout, shortcut: "Ctrl+F7");
            var old = defaults with { SavedRegions = [.. defaults.SavedRegions.Select(p => p.Id == interactive.Id ? interactive : p), fixedInput],
                PresetCaptureShortcuts = null };
            string path = Path.Combine(directory, "settings.json");
            string json = JsonSerializer.Serialize(old);
            File.WriteAllText(path, json);
            var store = new SettingsStore(path);
            var loaded = store.Load(out var warning);
            Equal<string?>(null, warning);
            Equal(json, File.ReadAllText(path));
            Check(loaded.PresetCaptureShortcuts!.SequenceEqual(
                [new("Ctrl+F6", interactive.Id), new("Ctrl+F7", fixedInput.Id, LegacyInstantOutput: true)]), "移行行の条件が異なる");
            Check(loaded.SavedRegions.All(p => p.Shortcut == ""), "旧キーの保存元が残った");
            var assignments = HotkeyBinding.ReadAssignments(loaded);
            Equal(2, assignments.Count(a => a.PresetKey is not null));
            var state = new HotkeyState(assignments, KeyboardModifiers.LeftControl);
            var pressed = state.Process((int)Keys.F7, true, true);
            Check(pressed.Capture && pressed.PresetKey == "Ctrl+F7" && pressed.RegionId is null, "静止画用の組み合わせを解決しない");
            Check(!state.Process((int)Keys.F7, true, true).Capture && !state.Process((int)Keys.F7, false, true).Capture, "一押下で複数発火");
            store.Save(loaded with { PresetCaptureShortcuts = [] });
            string?[] keys = store.Load(out warning).PresetCaptureShortcuts!.Select(p => p.Key).ToArray();
            Equal<string?>(null, warning);
            Equal(0, keys.Length);
            var stale = JsonNode.Parse(json)!.AsObject();
            stale[nameof(AppSettings.PresetCaptureShortcuts)] = new JsonArray();
            stale[nameof(AppSettings.SavedRegions)]!.AsArray()[^1]![nameof(SavedRegion.Shortcut)] = "Ctrl+UnknownLegacyKey";
            File.WriteAllText(path, stale.ToJsonString());
            loaded = store.Load(out warning);
            Equal<string?>(null, warning);
            Equal(0, loaded.PresetCaptureShortcuts!.Length);
            Check(!HotkeyBinding.ReadAssignments(loaded).Any(a => a.Binding.VirtualKey is (int)Keys.F6 or (int)Keys.F7), "空一覧から旧キーが復活");
        }));
        Run("同じ入力で2つの出力と既定参照を解決し名前変更や既定変更を追従", () =>
        {
            var existing = new AppSettings().ApplyDefaults();
            var input = existing.SavedRegions.First(p => !p.IsDefault) with { Name = "変更後の入力", IncludeCursor = true };
            var file = existing.CapturePresets.Single(p => p.Id == "initial-output-file") with { Name = "変更後の出力" };
            var settings = existing with
            {
                SavedRegions = existing.SavedRegions.Select(p => p.Id == input.Id ? input : p).ToArray(),
                CapturePresets = existing.CapturePresets.Select(p => p.Id == file.Id ? file : p).ToArray(),
                PresetCaptureShortcuts = [new("Ctrl+F6", input.Id, file.Id), new("Ctrl+F7", input.Id), new("Ctrl+F8")]
            };
            settings.Validate();
            ResolvedPresetCapture Resolve(PresetCaptureShortcut p, AppSettings current) => ResolvedPresetCapture.Resolve(current, p.InputPresetId, p.OutputPresetId);
            var rows = settings.PresetCaptureShortcuts;
            var resolved = Resolve(rows[0], settings);
            Equal("変更後の入力", resolved.Input.Name); Equal("変更後の出力", resolved.Output.Name);
            Equal(OutputDestination.File, resolved.Settings.DefaultDestination); Check(resolved.Settings.IncludeCursor, "入力条件を失った");
            Equal(OutputDestination.Clipboard, Resolve(rows[1], settings).Settings.DefaultDestination);
            var defaultsChanged = settings with
            {
                SavedRegions = settings.SavedRegions.Select(p => p with { IsDefault = p.Id == input.Id }).ToArray(),
                CapturePresets = settings.CapturePresets.Select(p => p with { IsDefault = p.Id == file.Id }).ToArray()
            };
            Equal(input.Id, Resolve(rows[2], defaultsChanged).Input.Id); Equal(file.Id, Resolve(rows[2], defaultsChanged).Output.Id);
            Check(!settings.SavedRegions.Single(p => p.Id == input.Id).IsDefault && settings.DefaultDestination == OutputDestination.Clipboard,
                "撮影するだけで既定を変更した");
            string? used = PresetCaptureShortcut.ReferenceError(rows, input.Id, input: true);
            Check(used?.Contains("Ctrl＋F6") == true && used.Contains("Ctrl＋F7") && !used.Contains("Ctrl＋F8"), "使用中の行を正しく案内しない");
            foreach (string competing in new[] { settings.ScreenshotShortcut1, settings.RecordingShortcut1, settings.InstantShortcuts[0], settings.LastRegionShortcut, "Ctrl+F6" })
                CaptureCommandReject(() => (settings with { PresetCaptureShortcuts = [.. rows, new(competing)] }).Validate());
            CaptureCommandReject(() => (settings with { PresetCaptureShortcuts = [new("Ctrl+F6", "missing")] }).Validate());
            CaptureCommandReject(() => (settings with { PresetCaptureShortcuts = [new("Ctrl+F6", OutputPresetId: "missing")] }).Validate());
        });
        Run("旧固定位置キーだけコピー・保存に制限し、明示出力では編集などを使える", () => WithServiceDirectory(directory => RunOutputTask(async () =>
        {
            var fixedInput = RegionCoordinates.FromBounds("固定", new(10, 20, 8, 6), RegionLayout, shortcut: "Ctrl+F6");
            var settings = new AppSettings { SavedRegions = [fixedInput], SaveDirectory = directory, FileNamePattern = "preset_{##}.png",
                DefaultDestination = OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp,
                OpenFolderAfterSave = true }.ApplyDefaults();
            var row = settings.PresetCaptureShortcuts!.Single();
            var legacy = ResolvedPresetCapture.Resolve(settings, row.InputPresetId, row.OutputPresetId, row.LegacyInstantOutput);
            int copies = 0, launches = 0;
            using var output = new OutputRouter(directory, _ => { copies++; return Task.CompletedTask; }, launch: _ => launches++);
            using var image = new Bitmap(8, 6);
            await InstantCapture.DeliverAsync(image, legacy.Settings, DateTimeOffset.Now, output, preserveOutputs: !legacy.LegacyInstantOutput);
            Equal(1, copies); Equal(0, launches);
            Check(File.Exists(Path.Combine(directory, "preset_01.png")), "旧固定位置キーの保存を失った");
            var modern = ResolvedPresetCapture.Resolve(settings, row.InputPresetId, legacy.Output.Id, row.LegacyInstantOutput);
            Check(!modern.LegacyInstantOutput && modern.Settings.OpenFolderAfterSave && modern.Settings.DefaultDestination.HasFlag(OutputDestination.ExternalApp),
                "明示出力へ切り替わらない");
        })));
        Run("組み合わせ画面で編集・保存失敗・再試行・参照削除・両プリセットからの入口を検証", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var input = defaults.SavedRegions.First(p => !p.IsDefault);
            var output = defaults.CapturePresets.Single(p => p.Id == "initial-output-file");
            AppSettings persisted = defaults with { PresetCaptureShortcuts = [new("Ctrl+F6", input.Id, output.Id)] };
            using var form = new SettingsForm(persisted, next =>
            {
                if (next.PresetCaptureShortcuts!.Any(p => p.Key == "Ctrl+F7")) return "試験用の保存エラー";
                persisted = next;
                return null;
            }, () => { }, () => []);
            T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
            void Click(string name) => CaptureCommandClick(form, name);
            string Category() => ((TabPage)Find<ListBox>("SettingsCategories").SelectedItem!).Text;
            Find<ListBox>("SavedRegions").SelectedIndex = Array.FindIndex(defaults.SavedRegions, p => p.Id == input.Id);
            Click("DeleteRegion");
            Check(Find<Label>("RegionStatus").Text.Contains("Ctrl＋F6"), "削除を止める行を案内しない");
            Click("OpenInputPresetShortcuts");
            Equal("ショートカット", Category());
            var grid = Find<DataGridView>("PresetCaptureShortcuts");
            Equal(input.Name, (string?)grid.Rows[0].Cells[1].Value);
            var key = Find<HotkeyEditor>("PresetCaptureShortcut");
            key.Value = "Ctrl+F7";
            SaveConsolidation(form);
            Equal("Ctrl+F6", persisted.PresetCaptureShortcuts!.Single().Key);
            Equal("Ctrl+F7", key.Value);
            key.Value = "Ctrl+F8";
            SaveConsolidation(form);
            Equal("Ctrl+F8", persisted.PresetCaptureShortcuts!.Single().Key);
            Find<ListBox>("CapturePresetList").SelectedIndex = Array.FindIndex(defaults.CapturePresets, p => p.Id == output.Id);
            Click("DeleteCapturePreset");
            Check(Find<Label>("PresetEditorStatus").Text.Contains("Ctrl＋F8"), "出力を参照する行を案内しない");
            Click("OpenOutputPresetShortcuts");
            Equal(output.Name, (string?)grid.Rows[0].Cells[2].Value);
            Click("DeletePresetCaptureShortcut");
            SaveConsolidation(form);
            Equal(0, persisted.PresetCaptureShortcuts!.Length);
            Click("DeleteRegion");
            Check(persisted.SavedRegions.All(p => p.Id != input.Id), "割当削除後も入力を削除できない");
            form.ShowPresetShortcuts();
            Click("AddPresetCaptureShortcut");
            key.Value = "Ctrl+F9";
            Find<ComboBox>("ShortcutInputPreset").SelectedIndex = 0;
            Find<ComboBox>("ShortcutOutputPreset").SelectedIndex = Array.FindIndex(defaults.CapturePresets, p => p.Id == output.Id) + 1;
            SaveConsolidation(form);
            Equal(new PresetCaptureShortcut("Ctrl+F9", OutputPresetId: output.Id), persisted.PresetCaptureShortcuts!.Single());
        });
    }
}
