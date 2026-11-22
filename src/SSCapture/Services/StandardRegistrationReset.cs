namespace SSCapture.Services;

internal static class StandardRegistrationReset
{
    internal static AppSettings RestoreInputs(AppSettings settings)
    {
        var defaults = new AppSettings().EnsurePresets().SavedRegions;
        if (settings.SavedRegions.Any(preset => preset.BuiltInKey == PresetSettings.InitialWindowKey))
            defaults = [..defaults, SavedRegion.FromSettings("ウィンドウ", new AppSettings { DefaultMode = CaptureMode.Window }) with
            { Id = PresetSettings.InitialWindowKey, BuiltInKey = PresetSettings.InitialWindowKey }];
        var regions = Restore(settings.SavedRegions, defaults,
            preset => (preset.Id, preset.Name, preset.BuiltInKey), MatchesLegacyInput,
            (template, previous, id, name) => template with { Id = id, Name = name, IsDefault = previous?.IsDefault == true });
        return settings with { SavedRegions = PresetSettings.OrderFixedPresets(regions) };
    }

    internal static AppSettings RestoreOutputs(AppSettings settings)
    {
        var defaults = new AppSettings().EnsurePresets();
        var paintTemplate = defaults.ImageEditors.Single();
        string paintId = settings.ImageEditors.FirstOrDefault(app => app.BuiltInKey == paintTemplate.BuiltInKey)?.Id
            ?? settings.ImageEditors.FirstOrDefault(app => MatchesLegacyPaint(app, paintTemplate))?.Id ?? "";
        var templates = defaults.CapturePresets.Select(preset => preset.IsDefault ? preset with { ImageEditorId = paintId } : preset);
        var presets = Restore(settings.CapturePresets, templates,
            preset => (preset.Id, preset.Name, preset.BuiltInKey), MatchesLegacyOutput,
            (template, previous, id, name) => template with { Id = id, Name = name, IsDefault = previous?.IsDefault == true });
        if (presets.Length > 100)
            throw new ArgumentException("出力プリセットは100件までです。標準プリセットを復元する空きがありません。");
        return settings with { CapturePresets = presets };
    }

    internal static AppSettings RestoreExternalApplications(AppSettings settings)
    {
        var template = new AppSettings().EnsurePresets().ImageEditors.Single();
        var apps = Restore(settings.ImageEditors, [template],
            app => (app.Id, app.Name, app.BuiltInKey), MatchesLegacyPaint,
            (standard, _, id, name) => standard with { Id = id, Name = name },
            settings.VideoEditors.Select(app => app.Id));
        if (apps.Length > 50)
            throw new ArgumentException("画像用の外部アプリは50件までです。標準の外部アプリを復元する空きがありません。");
        return settings with { ImageEditors = apps };
    }

    private static T[] Restore<T>(T[] current, IEnumerable<T> templates, Func<T, (string Id, string Name, string? Key)> identity,
        Func<T, T, bool> matchesLegacy, Func<T, T?, string, string, T> restore, IEnumerable<string>? reservedIds = null) where T : class
    {
        var steps = templates.Select(template =>
        {
            var key = identity(template).Key;
            int index = Array.FindIndex(current, entry => identity(entry).Key == key);
            bool add = index < 0 && !current.Any(entry => identity(entry).Key is null && matchesLegacy(entry, template));
            return (Template: template, Index: index, Add: add);
        }).Where(step => step.Index >= 0 || step.Add).ToArray();

        var replaced = steps.Where(step => step.Index >= 0).Select(step => step.Index).ToHashSet();
        var names = current.Where((_, index) => !replaced.Contains(index)).Select(entry => identity(entry).Name.Trim()).ToList();
        var ids = current.Select(entry => identity(entry).Id).Concat(reservedIds ?? []).ToList();
        var result = current.ToList();
        foreach (var step in steps)
        {
            var (templateId, templateName, _) = identity(step.Template);
            string name = PresetSettings.UniqueName(templateName, names);
            names.Add(name);
            if (step.Index >= 0)
            {
                var previous = current[step.Index];
                result[step.Index] = restore(step.Template, previous, identity(previous).Id, name);
            }
            else
            {
                string id = PresetSettings.UniqueId(templateId, ids);
                ids.Add(id);
                result.Add(restore(step.Template, null, id, name));
            }
        }
        return result.ToArray();
    }

    // 由来の記録がない項目は、過去の標準と一致しても書き換えず、現在の値だけで復元済みと判断する。
    private static bool MatchesLegacyInput(SavedRegion entry, SavedRegion template) =>
        (entry.Id == template.Id || (template.Id == PresetSettings.InitialFixedKey && entry.Id == "initial-input-fixed-1280-720"))
        && entry.Layout is { Length: 0 }
        && entry == (template with { Id = entry.Id, BuiltInKey = null, IsDefault = entry.IsDefault, Layout = entry.Layout });

    private static bool MatchesLegacyOutput(CapturePreset entry, CapturePreset template) =>
        (entry.Id == template.Id || (template.Id == PresetSettings.StandardOutputKey && entry.Id == "initial-output-clipboard"))
        && (entry with { ImageEditorId = entry.ImageEditorId ?? "", VideoEditorId = entry.VideoEditorId ?? "" }) ==
            (template with { Id = entry.Id, BuiltInKey = null, IsDefault = entry.IsDefault });

    private static bool MatchesLegacyPaint(ExternalApplication entry, ExternalApplication template) =>
        entry.BuiltInKey is null && entry.Id == template.Id &&
        (string.Equals(entry.Executable, EditorExecutable.LegacyPaint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Executable, EditorExecutable.PaintAlias, StringComparison.OrdinalIgnoreCase))
        && entry == (template with { BuiltInKey = null, Executable = entry.Executable });
}
