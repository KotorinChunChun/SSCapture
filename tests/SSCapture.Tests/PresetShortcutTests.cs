using System.Text.Json;
using System.Text.Json.Nodes;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void PresetShortcutTests(bool includeSettingsForm = true)
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
            foreach (string competing in new[] { settings.InstantShortcuts[0], "Ctrl+F6" })
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
            var row = settings.PresetCaptureShortcuts!.Single(p => p.InputPresetId == fixedInput.Id);
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
        Run("通常キーと既存の組み合わせを移行し、録画のキーと設定を再読込しても維持", () => WithServiceDirectory(directory =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var fixedInput = RegionCoordinates.FromBounds("登録位置", new(10, 20, 30, 40), RegionLayout) with
                { IsDefault = true, RecordSystemAudio = true, RecordMicrophone = true, MicrophoneDeviceId = "fixture-mic" };
            var file = defaults.CapturePresets.Single(p => p.Id == "initial-output-file");
            var clipboard = defaults.CapturePresets.Single(p => p.IsDefault);
            var old = defaults with
            {
                ScreenshotShortcut1 = "control + F5", ScreenshotShortcut2 = "Ctrl+F6",
                RecordingShortcut1 = "Ctrl+F7", RecordingShortcut2 = "Ctrl+F8",
                SavedRegions = [.. defaults.SavedRegions.Select(p => p with { IsDefault = false }), fixedInput],
                PresetCaptureShortcuts = [new("Ctrl+F9", fixedInput.Id, file.Id), new("Ctrl+F10", OutputPresetId: file.Id)]
            };
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, JsonSerializer.Serialize(old, SettingsStore.Options));
            var store = new SettingsStore(path);
            var loaded = store.Load(out string? warning);
            Equal<string?>(null, warning);
            PresetCaptureShortcut[] expected =
            [
                new("Ctrl+F9", fixedInput.Id, file.Id), new("Ctrl+F10", OutputPresetId: file.Id),
                new("Ctrl+F5") { SelectRangeWithDefaultInput = true }, new("Ctrl+F6") { SelectRangeWithDefaultInput = true },
                new("Ctrl+F7", Intent: CaptureIntent.Recording), new("Ctrl+F8", Intent: CaptureIntent.Recording)
            ];
            Check(loaded.PresetCaptureShortcuts!.SequenceEqual(expected), "既定参照・個別指定または撮影種別を移行できない");
            var keys = new HotkeyState(HotkeyBinding.ReadAssignments(loaded), KeyboardModifiers.LeftControl);
            Equal(new HotkeyDecision(true, true, CaptureIntent.Recording, PresetKey: "Ctrl+F7"), keys.Process((int)Keys.F7, true, true));
            Check(!keys.Process((int)Keys.F7, true, true).Capture && !keys.Process((int)Keys.F7, false, true).Capture, "連続イベントで録画が再発火");
            Check(keys.Process((int)Keys.F7, true, true).Capture, "次の押下が録画に届かない");
            Check(expected[0].UsesDirectCapture(fixedInput) && expected[1].UsesDirectCapture(fixedInput),
                "旧プリセットの固定座標を即時撮影できない");
            Check(!expected[2].UsesDirectCapture(fixedInput) && expected[2].UsesNormalSelection,
                "旧通常キーで既定が固定座標のときに選択画面を使わない");
            var recording = expected[4];
            Check(!(recording with { InputPresetId = fixedInput.Id }).UsesDirectCapture(fixedInput), "録画を静止画として即時撮影する");
            var nextInput = loaded.SavedRegions.First(p => !p.IsDefault);
            var changed = loaded with
            {
                SavedRegions = loaded.SavedRegions.Select(p => p with { IsDefault = p.Id == nextInput.Id }).ToArray(),
                CapturePresets = loaded.CapturePresets.Select(p => p with { IsDefault = p.Id == file.Id }).ToArray()
            };
            foreach (bool explicitInput in new[] { false, true })
            foreach (bool explicitOutput in new[] { false, true })
            {
                var resolved = ResolvedPresetCapture.Resolve(changed, explicitInput ? fixedInput.Id : null, explicitOutput ? clipboard.Id : null);
                Equal(explicitInput ? fixedInput.Id : nextInput.Id, resolved.Input.Id);
                Equal(explicitOutput ? OutputDestination.Clipboard : OutputDestination.File, resolved.Settings.DefaultDestination);
                Equal(explicitInput, resolved.Settings.RecordSystemAudio);
                Equal(explicitInput, resolved.Settings.RecordMicrophone);
                Equal(explicitInput ? "fixture-mic" : "", resolved.Settings.MicrophoneDeviceId);
            }
            var command = CaptureCommandLine.Resolve(new CaptureCommandRequest(), changed);
            Equal(nextInput.Id, command.InputPreset.Id); Equal(file.Id, command.OutputPreset!.Id);
            Equal<Rectangle?>(null, command.DirectBounds());
            store.Save(loaded);
            var reopened = store.Load(out warning);
            Equal<string?>(null, warning);
            Check(reopened.PresetCaptureShortcuts!.SequenceEqual(expected), "二度目の起動で移行行を再作成した");
            store.Save(reopened with { PresetCaptureShortcuts = [recording] });
            Equal(recording, store.Load(out _).PresetCaptureShortcuts!.Single());
            CaptureCommandReject(() => (loaded with { PresetCaptureShortcuts = [.. expected, new("ctrl+f7", Intent: CaptureIntent.Screenshot)] }).Validate());
            CaptureCommandReject(() => (loaded with { PresetCaptureShortcuts = [new("")] }).Validate());
            CaptureCommandReject(() => (loaded with { PresetCaptureShortcuts = [new("Ctrl+F5", Intent: (CaptureIntent)9)] }).Validate());
        }));

        if (!includeSettingsForm) return;
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
            }, _ => { }, () => []);
            T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
            void Click(string name) => CaptureCommandClick(form, name);
            string Category() => ((TabPage)Find<ListBox>("SettingsCategories").SelectedItem!).Text;
            Find<ListBox>("SavedRegions").SelectedIndex = Array.FindIndex(defaults.SavedRegions, p => p.Id == input.Id);
            Click("DeleteRegion");
            Check(Find<Label>("RegionStatus").Text.Contains("Ctrl＋F6"), "削除を止める行を案内しない");
            Click("OpenInputPresetShortcuts");
            Equal("ショートカット", Category());
            var grid = Find<DataGridView>("PresetCaptureShortcuts");
            Equal("静止画", (string?)grid.Rows[0].Cells[1].Value);
            Equal(input.Name, (string?)grid.Rows[0].Cells[2].Value);
            var key = Find<HotkeyEditor>("PresetCaptureShortcut");
            key.Value = "Ctrl+F7";
            Click("SavePresetCaptureShortcuts");
            Equal("Ctrl+F6", persisted.PresetCaptureShortcuts!.Single().Key);
            Equal("Ctrl+F7", key.Value);
            key.Value = "Ctrl+F8";
            Click("SavePresetCaptureShortcuts");
            Equal("Ctrl+F8", persisted.PresetCaptureShortcuts!.Single().Key);
            Find<ListBox>("CapturePresetList").SelectedIndex = Array.FindIndex(defaults.CapturePresets, p => p.Id == output.Id);
            Click("DeleteCapturePreset");
            Check(Find<Label>("PresetEditorStatus").Text.Contains("Ctrl＋F8"), "出力を参照する行を案内しない");
            Click("OpenOutputPresetShortcuts");
            Equal(output.Name, (string?)grid.Rows[0].Cells[3].Value);
            Click("DeletePresetCaptureShortcut");
            Click("SavePresetCaptureShortcuts");
            Equal(0, persisted.PresetCaptureShortcuts!.Length);
            Click("DeleteRegion");
            Check(persisted.SavedRegions.All(p => p.Id != input.Id), "割当削除後も入力を削除できない");
            form.ShowPresetShortcuts();
            Click("AddPresetCaptureShortcut");
            key.Value = "Ctrl+F9";
            Find<ComboBox>("ShortcutCaptureIntent").SelectedIndex = (int)CaptureIntent.Recording;
            Find<ComboBox>("ShortcutInputPreset").SelectedIndex = 0;
            Find<ComboBox>("ShortcutOutputPreset").SelectedIndex = Array.FindIndex(defaults.CapturePresets, p => p.Id == output.Id) + 1;
            Click("SavePresetCaptureShortcuts");
            var recording = new PresetCaptureShortcut("Ctrl+F9", OutputPresetId: output.Id, Intent: CaptureIntent.Recording)
                { SelectRangeWithDefaultInput = true };
            Equal(recording, persisted.PresetCaptureShortcuts!.Single());

            bool previousDim = persisted.DimCaptureBackground;
            Find<CheckBox>("DimCaptureBackground").Checked = !previousDim;
            Click("DuplicatePresetCaptureShortcut");
            Equal(2, grid.Rows.Count);
            Equal("", key.Value);
            Equal("録画", (string?)grid.Rows[1].Cells[1].Value);
            Equal(output.Name, (string?)grid.Rows[1].Cells[3].Value);
            Click("SavePresetCaptureShortcuts");
            Equal(recording, persisted.PresetCaptureShortcuts!.Single());
            key.Value = "Ctrl+F10";
            Click("SavePresetCaptureShortcuts");
            Equal(recording with { Key = "Ctrl+F10" }, persisted.PresetCaptureShortcuts![1]);
            Equal(previousDim, persisted.DimCaptureBackground);
        });
        Run("割り当て表示は入力と出力の直接指定・既定経由・確定した変更に追従する", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var input = defaults.SavedRegions.First(preset => !preset.IsDefault);
            var output = defaults.CapturePresets.First(preset => !preset.IsDefault);
            var settings = defaults with { PresetCaptureShortcuts = [new("Ctrl+F6"), new("Ctrl+F7", input.Id, output.Id)] };
            using var form = new SettingsForm(settings, _ => null, _ => { }, () => []);
            var inputLink = form.Controls.Find("OpenInputPresetShortcuts", true).Single();
            var outputLink = form.Controls.Find("OpenOutputPresetShortcuts", true).Single();
            Equal("Ctrl＋F6", inputLink.Text);
            Equal("Ctrl＋F6", outputLink.Text);
            var inputs = (ListBox)form.Controls.Find("SavedRegions", true).Single();
            var outputs = (ListBox)form.Controls.Find("CapturePresetList", true).Single();
            inputs.SelectedItem = inputs.Items.Cast<SavedRegion>().Single(preset => preset.Id == input.Id);
            outputs.SelectedItem = outputs.Items.Cast<CapturePreset>().Single(preset => preset.Id == output.Id);
            Equal("Ctrl＋F7", inputLink.Text);
            Equal("Ctrl＋F7", outputLink.Text);

            CaptureCommandClick(form, "OpenInputPresetShortcuts");
            var key = (HotkeyEditor)form.Controls.Find("PresetCaptureShortcut", true).Single();
            Equal("Ctrl+F7", key.Value);
            key.Value = "Ctrl+F8";
            Equal("Ctrl＋F7", inputLink.Text);
            CaptureCommandClick(form, "SavePresetCaptureShortcuts");
            Equal("Ctrl＋F8", inputLink.Text);
            Equal("Ctrl＋F8", outputLink.Text);

            CaptureCommandClick(form, "DefaultInputPreset");
            Equal("Ctrl＋F6、Ctrl＋F8", inputLink.Text);
            Equal("Ctrl＋F8", outputLink.Text);
            inputs.SelectedItem = inputs.Items.Cast<SavedRegion>().First(preset => !preset.IsDefault);
            Equal("割り当てなし", inputLink.Text);
        });
    }
}
