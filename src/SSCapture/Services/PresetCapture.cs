namespace SSCapture.Services;

internal sealed record PresetCaptureShortcut(string Key, string? InputPresetId = null, string? OutputPresetId = null,
    bool LegacyInstantOutput = false, CaptureIntent Intent = CaptureIntent.Screenshot)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SelectRangeWithDefaultInput { get; init; }

    internal string IntentLabel => Intent == CaptureIntent.Recording ? "録画" : "静止画";
    internal string Label(int index) => $"{IntentLabel}の割り当て {index + 1}（{KeyDisplay.Binding(Key)}）";
    internal bool UsesNormalSelection => Intent == CaptureIntent.Screenshot && InputPresetId is null && SelectRangeWithDefaultInput;
    internal bool UsesDirectCapture(SavedRegion input) =>
        Intent == CaptureIntent.Screenshot && !UsesNormalSelection && input.UseFixedCoordinates;

    internal static (string Label, string Key, CaptureIntent Intent)[] LegacySlots(AppSettings settings) =>
    [
        ("静止画のキー1", settings.ScreenshotShortcut1, CaptureIntent.Screenshot),
        ("静止画のキー2", settings.ScreenshotShortcut2, CaptureIntent.Screenshot),
        ("録画のキー1", settings.RecordingShortcut1, CaptureIntent.Recording),
        ("録画のキー2", settings.RecordingShortcut2, CaptureIntent.Recording)
    ];

    internal static AppSettings Migrate(AppSettings settings)
    {
        var rows = settings.PresetCaptureShortcuts ?? settings.SavedRegions
            .Where(region => !string.IsNullOrWhiteSpace(region.Shortcut))
            .Select(region => new PresetCaptureShortcut(region.Shortcut, region.Id, LegacyInstantOutput: region.UseFixedCoordinates))
            .ToArray();
        var legacySlots = LegacySlots(settings);
        // 旧通常キーは既定入力が登録座標でも選択画面を開いていた。
        var normalized = rows.Concat(legacySlots.Where(slot => !string.IsNullOrWhiteSpace(slot.Key))
            .Select(slot => new PresetCaptureShortcut(slot.Key, Intent: slot.Intent)
            {
                SelectRangeWithDefaultInput = slot.Intent == CaptureIntent.Screenshot
            })).Select(Normalize).ToArray();
        bool hasLegacyRegionKeys = settings.SavedRegions.Any(region => !string.IsNullOrEmpty(region.Shortcut));
        if (settings.PresetCaptureShortcuts is not null && !hasLegacyRegionKeys &&
            legacySlots.All(slot => slot.Key == "") && rows.SequenceEqual(normalized)) return settings;
        return settings with
        {
            PresetCaptureShortcuts = normalized,
            ScreenshotShortcut1 = "", ScreenshotShortcut2 = "",
            RecordingShortcut1 = "", RecordingShortcut2 = "",
            SavedRegions = hasLegacyRegionKeys ? settings.SavedRegions.Select(region => string.IsNullOrEmpty(region.Shortcut)
                ? region : region with { Shortcut = "" }).ToArray() : settings.SavedRegions
        };
    }

    internal static PresetCaptureShortcut? FindDefaultFixedAssignment(AppSettings settings)
    {
        const string key = "Win+Shift+Q";
        if (settings.SavedRegions.FirstOrDefault(region => region.BuiltInKey == PresetSettings.InitialFixedKey) is not { } preset)
            return null;
        var used = (settings.PresetCaptureShortcuts ?? []).Select(row => row.Key)
            .Concat(settings.InstantShortcuts).Append(settings.LastRecipeShortcut);
        return used.Any(value => HotkeyBinding.Parse(value) == HotkeyBinding.Parse(key)) ? null : new(key, preset.Id);
    }

    private static PresetCaptureShortcut Normalize(PresetCaptureShortcut row, int index)
    {
        try
        {
            if (HotkeyBinding.Parse(row?.Key) is not { } key)
                throw new ArgumentException("ショートカットキーを指定してください。");
            return row! with { Key = key.ToString(), LegacyInstantOutput = row.Intent == CaptureIntent.Screenshot &&
                !row.UsesNormalSelection && row.OutputPresetId is null && row.LegacyInstantOutput };
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"プリセット撮影 {index + 1}: {ex.Message}", ex);
        }
    }

    internal static string? ReferenceError(PresetCaptureShortcut[] rows, string id, bool input)
    {
        int[] indices = Enumerable.Range(0, rows.Length)
            .Where(index => id == (input ? rows[index].InputPresetId : rows[index].OutputPresetId)).ToArray();
        if (indices.Length == 0) return null;
        string referencedBy = string.Join("、", indices.Select(index => rows[index].Label(index)));
        return $"{referencedBy}で使用中です。ショートカットの割り当てを変更または削除し、保存してください。";
    }
}

internal sealed record ResolvedPresetCapture(SavedRegion Input, CapturePreset Output, AppSettings Settings, bool LegacyInstantOutput)
{
    internal static ResolvedPresetCapture Resolve(AppSettings settings, string? inputId, string? outputId,
        bool legacyInstantOutput = false)
    {
        settings = settings.EnsurePresets();
        SavedRegion input = inputId is null ? settings.SavedRegions.Single(p => p.IsDefault)
            : settings.SavedRegions.SingleOrDefault(p => p.Id == inputId)
                ?? throw new ArgumentException("入力プリセットが見つかりません: " + inputId);
        CapturePreset output = outputId is null ? settings.CapturePresets.Single(p => p.IsDefault)
            : settings.CapturePresets.SingleOrDefault(p => p.Id == outputId)
                ?? throw new ArgumentException("出力プリセットが見つかりません: " + outputId);
        return new(input, output, output.Apply(input.Apply(settings)).ResolveEditors(),
            legacyInstantOutput && input.UseFixedCoordinates && outputId is null);
    }
}
