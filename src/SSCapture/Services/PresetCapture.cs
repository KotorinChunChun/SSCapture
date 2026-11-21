namespace SSCapture.Services;

internal sealed record PresetCaptureShortcut(string Key, string? InputPresetId = null, string? OutputPresetId = null,
    bool LegacyInstantOutput = false)
{
    internal string Label(int index) => $"プリセット撮影 {index + 1}（{KeyDisplay.Binding(Key)}）";

    internal static AppSettings Migrate(AppSettings settings)
    {
        var rows = settings.PresetCaptureShortcuts ?? settings.SavedRegions
            .Where(region => !string.IsNullOrWhiteSpace(region.Shortcut))
            .Select(region => new PresetCaptureShortcut(region.Shortcut, region.Id, LegacyInstantOutput: region.UseFixedCoordinates))
            .ToArray();
        var normalized = rows.Select(Normalize).ToArray();
        bool hasLegacyKeys = settings.SavedRegions.Any(region => !string.IsNullOrEmpty(region.Shortcut));
        if (settings.PresetCaptureShortcuts is not null && !hasLegacyKeys && rows.SequenceEqual(normalized)) return settings;
        return settings with
        {
            PresetCaptureShortcuts = normalized,
            SavedRegions = hasLegacyKeys ? settings.SavedRegions.Select(region => string.IsNullOrEmpty(region.Shortcut)
                ? region : region with { Shortcut = "" }).ToArray() : settings.SavedRegions
        };
    }

    private static PresetCaptureShortcut Normalize(PresetCaptureShortcut row, int index)
    {
        try
        {
            if (HotkeyBinding.Parse(row?.Key) is not { } key)
                throw new ArgumentException("ショートカットキーを指定してください。");
            return row! with { Key = key.ToString(), LegacyInstantOutput = row.OutputPresetId is null && row.LegacyInstantOutput };
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
