namespace SSCapture.Services;

internal readonly partial record struct HotkeyBinding
{
    public static void ValidateSettings(AppSettings settings) => _ = ReadAssignments(settings);

    internal static HotkeyAssignment[] ReadAssignments(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InstantShortcuts is null || settings.InstantShortcuts.Length != 10)
            throw new ArgumentException("即時撮影のショートカットはディスプレイ1～10の10件を指定してください。");
        if (settings.SavedRegions is null || settings.SavedRegions.Any(region => region is null)) throw new ArgumentException("登録範囲が不正です。");
        List<(string Label, string? Text, CaptureIntent Intent, int DisplayNumber, string? RegionId)> slots =
        [
            ("直前の設定で再撮影", settings.LastRecipeShortcut, CaptureIntent.Screenshot, 0, AppSettings.LastRecipeKeyId)
        ];
        slots.AddRange(PresetCaptureShortcut.LegacySlots(settings).Select(slot => (slot.Label, (string?)slot.Key, slot.Intent, 0, (string?)null)));
        slots.AddRange(settings.InstantShortcuts.Select((text, index) => ($"即時撮影 {index + 1}", (string?)text, CaptureIntent.Screenshot, index + 1, (string?)null)));
        if (settings.PresetCaptureShortcuts is null)
            slots.AddRange(settings.SavedRegions.Select(region => ($"登録範囲「{region.Name}」", (string?)region.Shortcut, CaptureIntent.Screenshot, 0, (string?)region.Id)));
        var assignments = new List<HotkeyAssignment>(14);
        var used = new Dictionary<HotkeyBinding, string>();
        foreach (var (label, text, intent, displayNumber, regionId) in slots)
        {
            HotkeyBinding? parsed;
            try { parsed = Parse(text); }
            catch (ArgumentException ex) { throw new ArgumentException($"{label}: {ex.Message}", ex); }
            if (parsed is not HotkeyBinding binding) continue;
            if (used.TryGetValue(binding, out string? first))
                throw new ArgumentException($"{label}と{first}のショートカットが重複しています（{KeyDisplay.Binding(binding)}）。");
            used.Add(binding, label);
            assignments.Add(new HotkeyAssignment(binding, intent, displayNumber, regionId));
        }
        if (settings.PresetCaptureShortcuts is { } rows)
        {
            for (int index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                string label = $"プリセット撮影 {index + 1}";
                try
                {
                    var binding = Parse(row?.Key) ?? throw new ArgumentException("ショートカットキーを指定してください。");
                    if (!Enum.IsDefined(row!.Intent)) throw new ArgumentException("撮影種別が正しくありません。");
                    label = row.Label(index);
                    if (row.InputPresetId is { } input && !settings.SavedRegions.Any(p => p.Id == input))
                        throw new ArgumentException("入力プリセットが見つかりません: " + input);
                    if (row.OutputPresetId is { } output && settings.CapturePresets?.Any(p => p?.Id == output) != true)
                        throw new ArgumentException("出力プリセットが見つかりません: " + output);
                    if (used.TryGetValue(binding, out string? first))
                        throw new ArgumentException($"{first}とショートカットが重複しています。");
                    used.Add(binding, label);
                    assignments.Add(new(binding, row.Intent, PresetKey: binding.ToString()));
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"{label}: {ex.Message}", ex);
                }
            }
        }
        return assignments.ToArray();
    }
}
