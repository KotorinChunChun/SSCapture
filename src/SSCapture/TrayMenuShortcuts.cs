using SSCapture.Services;

namespace SSCapture;

internal static class TrayMenuShortcuts
{
    internal static string Format(IEnumerable<string?> keys) => FormatBindings(keys.Select(HotkeyBinding.Parse).OfType<HotkeyBinding>());

    internal static string Capture(AppSettings settings, CaptureIntent intent, bool recordingActive = false)
    {
        var input = settings.SavedRegions.FirstOrDefault(preset => preset.IsDefault);
        string? outputId = settings.CapturePresets.FirstOrDefault(preset => preset.IsDefault)?.Id;
        var keys = PresetCaptureShortcut.LegacySlots(settings).Where(slot => slot.Intent == intent).Select(slot => slot.Key);
        var presets = (settings.PresetCaptureShortcuts ?? []).Where(row => row.Intent == intent &&
            (recordingActive || input is not null && (row.InputPresetId ?? input.Id) == input.Id
                && (row.OutputPresetId ?? outputId) == outputId && !row.UsesDirectCapture(input)));
        return Format(keys.Concat(presets.Select(row => row.Key)));
    }

    internal static string Region(AppSettings settings, SavedRegion region)
    {
        string? inputId = settings.SavedRegions.FirstOrDefault(preset => preset.IsDefault)?.Id;
        string? outputId = settings.CapturePresets.FirstOrDefault(preset => preset.IsDefault)?.Id;
        var keys = (settings.PresetCaptureShortcuts ?? []).Where(row =>
            row.Intent == CaptureIntent.Screenshot && (row.InputPresetId ?? inputId) == region.Id
            && (region.UseFixedCoordinates
                ? row.UsesDirectCapture(region) && row.LegacyInstantOutput && row.OutputPresetId is null
                : (row.OutputPresetId ?? outputId) == outputId));
        return Format(keys.Select(row => row.Key));
    }

    private static string FormatBindings(IEnumerable<HotkeyBinding> bindings) =>
        string.Join(" / ", bindings.Distinct().Select(KeyDisplay.Binding));

    internal static void UpdateSeparators(ToolStripItemCollection items)
    {
        bool hasVisibleItem = false;
        ToolStripSeparator? pending = null;
        foreach (ToolStripItem item in items)
        {
            if (item is ToolStripSeparator separator)
            {
                separator.Available = false;
                if (hasVisibleItem) pending ??= separator;
            }
            else if (item.Available)
            {
                if (pending is not null) pending.Available = true;
                pending = null;
                hasVisibleItem = true;
            }
        }
    }
}
