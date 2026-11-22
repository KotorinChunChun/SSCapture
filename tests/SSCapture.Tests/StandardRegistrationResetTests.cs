using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void StandardRegistrationResetTests()
    {
        Run("新規設定の標準登録はすべて由来を持ち復元後も初期内容が一致する", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            Equal(4, defaults.SavedRegions.Length);
            Equal(3, defaults.CapturePresets.Length);
            Equal(1, defaults.ImageEditors.Length);
            Equal(0, defaults.VideoEditors.Length);
            Check(defaults.SavedRegions.All(p => p.BuiltInKey is not null), "標準入力の由来がない");
            Check(defaults.CapturePresets.All(p => p.BuiltInKey is not null), "標準出力の由来がない");
            Equal("builtin-paint", defaults.ImageEditors.Single().BuiltInKey);
            Equal(ResetSnapshot(defaults), ResetSnapshot(RestoreAllRegistrationPages(defaults)));
        });

        Run("標準入力と出力の改名・設定変更を戻して登録IDと撮影キーの参照を維持する", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var input = defaults.SavedRegions.Single(p => p.IsDefault);
            var output = defaults.CapturePresets.Single(p => p.IsDefault);
            var customInput = SavedRegion.FromSettings("自作入力", defaults);
            var customOutput = CapturePreset.FromSettings("自作出力", defaults);
            PresetCaptureShortcut[] shortcuts = [new("Ctrl+F11", input.Id, output.Id)];
            var changed = defaults with
            {
                SavedRegions = [input with { Name = "改名した入力", IncludeCursor = true }, customInput],
                CapturePresets = [output with { Name = "改名した出力", Destination = OutputDestination.File }, customOutput],
                PresetCaptureShortcuts = shortcuts
            };

            var inputReset = StandardRegistrationReset.RestoreInputs(changed);
            Check(ReferenceEquals(changed.CapturePresets, inputReset.CapturePresets), "出力ページの内容に触れた");
            var restored = StandardRegistrationReset.RestoreOutputs(inputReset).ApplyDefaults();
            restored.Validate();
            Equal(input, restored.SavedRegions.Single(p => p.Id == input.Id));
            Equal(output, restored.CapturePresets.Single(p => p.Id == output.Id));
            Check(ReferenceEquals(customInput, restored.SavedRegions.Single(p => p.Id == customInput.Id)), "自作入力が変わった");
            Check(ReferenceEquals(customOutput, restored.CapturePresets.Single(p => p.Id == customOutput.Id)), "自作出力が変わった");
            Check(ReferenceEquals(shortcuts, restored.PresetCaptureShortcuts), "撮影キーを変更した");
            var resolved = ResolvedPresetCapture.Resolve(restored, shortcuts[0].InputPresetId, shortcuts[0].OutputPresetId);
            Equal(input, resolved.Input);
            Equal(output, resolved.Output);
        });

        Run("削除した標準登録は自作の名前とIDを優先して補い二度目も同じ結果になる", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var standardInput = defaults.SavedRegions.Single(p => p.Id == "initial-input-fixed");
            var standardOutput = defaults.CapturePresets.Single(p => p.Id == "initial-output-file");
            var customInput = standardInput with { BuiltInKey = null, IncludeCursor = true, IsDefault = true };
            var customOutput = standardOutput with { BuiltInKey = null, Name = standardOutput.Name + " ", OpenFolderAfterSave = true, IsDefault = true };
            var changed = defaults with { SavedRegions = [customInput], CapturePresets = [customOutput] };
            var restored = RestoreAllRegistrationPages(changed).ApplyDefaults();
            restored.Validate();
            Equal(customInput, restored.SavedRegions.Single(p => p.IsDefault));
            Equal(customOutput, restored.CapturePresets.Single(p => p.IsDefault));
            var fixedInput = restored.SavedRegions.Single(p => p.BuiltInKey == standardInput.BuiltInKey);
            Equal(standardInput with { Id = "initial-input-fixed-2", Name = standardInput.Name + " 2" }, fixedInput);
            var fileOutput = restored.CapturePresets.Single(p => p.BuiltInKey == standardOutput.BuiltInKey);
            Equal(standardOutput with { Id = "initial-output-file-2", Name = standardOutput.Name + " 2" }, fileOutput);
            Equal(ResetSnapshot(restored), ResetSnapshot(RestoreAllRegistrationPages(restored)));
            Equal(customInput, changed.SavedRegions.Single());
            Equal(customOutput, changed.CapturePresets.Single());
        });

        Run("改名した標準ペイントは重複回避IDを維持し通知と出力の参照先を復元する", () =>
        {
            var custom = new ExternalApplication { Id = "builtin-paint", Name = "ペイント", Executable = @"C:\custom.exe" };
            var defaults = new AppSettings { ImageEditors = [custom] }.ApplyDefaults();
            var paint = defaults.ImageEditors.Single(p => p.BuiltInKey == "builtin-paint");
            Equal("builtin-paint-2", paint.Id);
            var output = CapturePreset.FromSettings("自作出力", defaults) with { ImageEditorId = paint.Id };
            var changed = defaults with
            {
                ImageEditors = [custom, paint with { Name = "別名", Executable = @"C:\other.exe", Arguments = "--open {file}" }],
                CapturePresets = [..defaults.CapturePresets, output],
                NotificationApplicationId = paint.Id
            };
            var restored = StandardRegistrationReset.RestoreExternalApplications(changed);
            restored.Validate();
            Equal(paint, restored.ImageEditors.Single(p => p.Id == paint.Id));
            Check(ReferenceEquals(custom, restored.ImageEditors[0]), "自作アプリが変わった");
            Check(ReferenceEquals(changed.VideoEditors, restored.VideoEditors), "動画の登録一覧に触れた");
            Check(ReferenceEquals(changed.CapturePresets, restored.CapturePresets), "出力プリセットに触れた");
            Equal(paint.Id, restored.NotificationApplicationId);
        });

        Run("旧登録は標準の名前・ID・実行ファイルを持つ場合も更新せず標準を補う", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var legacyInput = defaults.SavedRegions.Single(p => p.IsDefault) with { BuiltInKey = null, Name = "旧入力", IncludeCursor = true };
            var legacyOutput = defaults.CapturePresets.Single(p => p.IsDefault) with { BuiltInKey = null, Name = "旧出力", OcrSeparator = "-" };
            var legacyPaint = defaults.ImageEditors.Single() with { BuiltInKey = null, Arguments = "--open {file}" };
            var legacyVideo = new ExternalApplication { Id = "builtin-paint-2", Name = "動画アプリ", Executable = @"C:\video.exe" };
            var legacy = defaults with
            {
                SavedRegions = [legacyInput], CapturePresets = [legacyOutput], ImageEditors = [legacyPaint], VideoEditors = [legacyVideo]
            };
            var restored = RestoreAllRegistrationPages(legacy);
            restored.Validate();
            Check(ReferenceEquals(legacyInput, restored.SavedRegions.Single(p => p.IsDefault)), "由来不明の旧入力を変更した");
            Check(ReferenceEquals(legacyOutput, restored.CapturePresets.Single(p => p.IsDefault)), "由来不明の旧出力を変更した");
            Check(ReferenceEquals(legacyPaint, restored.ImageEditors[0]), "由来不明の旧画像アプリを変更した");
            Check(ReferenceEquals(legacyVideo, restored.VideoEditors.Single()), "旧動画アプリを変更した");
            Equal("builtin-paint-3", restored.ImageEditors.Single(p => p.BuiltInKey is not null).Id);
            Equal(legacyPaint.Id, restored.SelectedImageEditorId);
            var repeated = RestoreAllRegistrationPages(restored);
            Equal(ResetSnapshot(restored), ResetSnapshot(repeated));
        });

        Run("標準値と一致する由来不明の旧登録は重複作成も由来の書き換えもしない", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var legacy = defaults with
            {
                SavedRegions = defaults.SavedRegions.Select(p => p with { BuiltInKey = null }).ToArray(),
                CapturePresets = defaults.CapturePresets.Select(p => p with { BuiltInKey = null,
                    ImageEditorId = string.IsNullOrEmpty(p.ImageEditorId) ? null : p.ImageEditorId, VideoEditorId = null }).ToArray(),
                ImageEditors = defaults.ImageEditors.Select(p => p with { BuiltInKey = null }).ToArray()
            };
            var restored = RestoreAllRegistrationPages(legacy);
            Equal(ResetSnapshot(legacy), ResetSnapshot(restored));
        });

        Run("旧設定のペイント再利用は由来の印を付けず標準とは別に保持する", () =>
        {
            var reused = new ExternalApplication { Id = "old-paint", Name = "利用中のペイント", Executable = EditorExecutable.LegacyPaint };
            var defaults = new AppSettings { ImageEditors = [reused], SelectedImageEditorId = reused.Id }.ApplyDefaults();
            Equal(reused, defaults.ImageEditors.Single());
            var restored = StandardRegistrationReset.RestoreExternalApplications(defaults);
            Equal(reused, restored.ImageEditors[0]);
            Equal(reused.Id, restored.SelectedImageEditorId);
            Equal("builtin-paint", restored.ImageEditors[1].BuiltInKey);
        });

        Run("標準の由来を既存の設定保存と読込で保持し削除済み登録は明示操作まで戻さない", () =>
        {
            string directory = Directory.CreateTempSubdirectory("sscapture-reset-").FullName;
            try
            {
                var store = new SettingsStore(Path.Combine(directory, "settings.json"));
                var defaults = new AppSettings().ApplyDefaults();
                var input = defaults.SavedRegions.Single(p => p.IsDefault);
                var output = defaults.CapturePresets.Single(p => p.IsDefault);
                var changed = defaults with
                {
                    SavedRegions = [input with { Name = "保存した入力" }],
                    PresetCaptureShortcuts = defaults.PresetCaptureShortcuts!.Where(row => row.InputPresetId is null).ToArray(),
                    CapturePresets = [output with { Name = "保存した出力" }],
                    ImageEditors = [defaults.ImageEditors[0] with { Name = "保存したアプリ" }]
                };
                store.Save(changed.ApplyDefaults());
                var loaded = store.Load(out var warning);
                Equal<string?>(null, warning);
                Equal(input.BuiltInKey, loaded.SavedRegions.Single().BuiltInKey);
                Equal(output.BuiltInKey, loaded.CapturePresets.Single().BuiltInKey);
                Equal(defaults.ImageEditors[0].BuiltInKey, loaded.ImageEditors.Single().BuiltInKey);
                var restored = RestoreAllRegistrationPages(loaded);
                Equal(input.Name, restored.SavedRegions.Single(p => p.Id == input.Id).Name);
                Equal(output.Name, restored.CapturePresets.Single(p => p.Id == output.Id).Name);
                Equal(defaults.ImageEditors[0].Name, restored.ImageEditors.Single().Name);
                Equal("保存した出力", store.Load(out _).CapturePresets.Single().Name);
            }
            finally { Directory.Delete(directory, recursive: true); }
        });

        Run("登録上限では自作を捨てず復元候補を確定しない", () =>
        {
            var defaults = new AppSettings().ApplyDefaults();
            var outputs = Enumerable.Range(0, 100).Select(index =>
                CapturePreset.FromSettings("自作 " + index, defaults) with { IsDefault = index == 0 }).ToArray();
            var apps = Enumerable.Range(0, 50).Select(index =>
                new ExternalApplication { Name = "自作 " + index, Executable = @"C:\custom.exe" }).ToArray();
            var changed = defaults with { CapturePresets = outputs, ImageEditors = apps };
            try { StandardRegistrationReset.RestoreOutputs(changed); throw new Exception("出力の上限を超えて復元した"); }
            catch (ArgumentException ex) { Check(ex.Message.Contains("100件"), "出力の上限が示されない"); }
            try { StandardRegistrationReset.RestoreExternalApplications(changed); throw new Exception("画像アプリの上限を超えて復元した"); }
            catch (ArgumentException ex) { Check(ex.Message.Contains("50件"), "外部アプリの上限が示されない"); }
            Check(ReferenceEquals(outputs, changed.CapturePresets) && ReferenceEquals(apps, changed.ImageEditors), "元の登録を変更した");
        });
    }

    private static AppSettings RestoreAllRegistrationPages(AppSettings settings) =>
        StandardRegistrationReset.RestoreOutputs(StandardRegistrationReset.RestoreExternalApplications(StandardRegistrationReset.RestoreInputs(settings)));

    private static string ResetSnapshot(AppSettings settings) => System.Text.Json.JsonSerializer.Serialize(settings, SettingsStore.Options);
}
