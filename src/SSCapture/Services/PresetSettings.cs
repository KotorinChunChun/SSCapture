namespace SSCapture.Services;

internal static class PresetSettings
{
    internal const string StandardInputKey = "standard-input";
    internal const string StandardOutputKey = "standard-output";
    internal const string PaintKey = "builtin-paint";
    internal const string InitialWindowKey = "initial-input-window";
    internal const string InitialFixedKey = "initial-input-fixed";
    internal const int InitialFixedWidth = 960;
    internal const int InitialFixedHeight = 540;
    internal static Size InitialFixedSize => new(InitialFixedWidth, InitialFixedHeight);

    private static readonly (string Key, Size OldSize, Size NewSize)[] FixedTemplates =
    [
        (InitialFixedKey, new(1280, 720), InitialFixedSize),
        ("initial-input-fixed-800-600", new(1920, 1080), new(800, 600)),
        ("initial-input-fixed-320-240", new(2560, 1440), new(320, 240))
    ];

    internal static AppSettings EnsurePresets(this AppSettings settings)
    {
        bool newAssignments = settings.PresetSchemaVersion == 0 && settings.PresetCaptureShortcuts is null
            && settings.SavedRegions.Length == 0 && settings.ScreenshotShortcut1 == new AppSettings().ScreenshotShortcut1;
        settings = EnsureExternalApplications(settings);
        if (settings.PresetSchemaVersion >= 2) return CompletePresets(settings, newAssignments);
        settings = EnsureLegacyPresets(settings);
        var inputs = settings.SavedRegions.ToList();
        var outputs = settings.CapturePresets.ToList();
        var baseline = new AppSettings();
        foreach (var (name, id, key, mode) in new[]
        {
            ("ウィンドウ", InitialWindowKey, InitialWindowKey, CaptureMode.Window),
            ("固定サイズ 960×540", InitialFixedKey, InitialFixedKey, CaptureMode.FixedRegion),
            ("矩形選択", "initial-input-rectangle", StandardInputKey, CaptureMode.Rectangle)
        })
        {
            if (inputs.Count >= 3) break;
            if (inputs.Any(p => !p.UseFixedCoordinates && p.Mode == mode)) continue;
            inputs.Add(SavedRegion.FromSettings(UniqueName(name, inputs.Select(p => p.Name)), baseline with { DefaultMode = mode })
                with { Id = UniqueId(id, inputs.Select(p => p.Id)), BuiltInKey = key });
        }
        foreach (var (name, id, key, destination) in new[]
        {
            ("ファイル保存", "initial-output-file", "initial-output-file", OutputDestination.File),
            ("クリップボード＋ファイル", "initial-output-both", "initial-output-both", OutputDestination.Clipboard | OutputDestination.File),
            ("クリップボード", "initial-output-clipboard", StandardOutputKey, OutputDestination.Clipboard)
        })
        {
            if (outputs.Count >= 3) break;
            if (outputs.Any(p => p.Destination == destination)) continue;
            outputs.Add(CapturePreset.FromSettings(UniqueName(name, outputs.Select(p => p.Name)), baseline with { DefaultDestination = destination })
                with { Id = UniqueId(id, outputs.Select(p => p.Id)), BuiltInKey = key });
        }
        // 削除した初期候補を次の起動で復活させない。既定や既存の登録内容には触れない。
        return CompletePresets(settings with { PresetSchemaVersion = 2, SavedRegions = inputs.ToArray(), CapturePresets = outputs.ToArray() }, newAssignments);
    }
    private static AppSettings CompletePresets(AppSettings settings, bool newAssignments)
    {
        settings = PresetCaptureShortcut.Migrate(UpgradeFixedSizes(UpgradePresets(settings)));
        if (!newAssignments || PresetCaptureShortcut.FindDefaultFixedAssignment(settings) is not { } assignment) return settings;
        return settings with { PresetCaptureShortcuts = [.. settings.PresetCaptureShortcuts!, assignment] };
    }

    private static AppSettings UpgradePresets(AppSettings settings)
    {
        if (settings.PresetSchemaVersion >= 3) return settings;
        var baseline = new AppSettings();
        bool Untouched(SavedRegion p, CaptureMode mode, string name) => p.Name == name && !p.UseFixedCoordinates && p.Mode == mode &&
            (p.Bounds == new Rectangle(Point.Empty, InitialFixedSize) || p.Bounds == new Rectangle(0, 0, 1280, 720)) && p.Shortcut == "" &&
            p.FixedAnchor == CaptureAnchor.TopLeft && p.FreezeFrame && !p.IncludeCursor && p.PaddingPixels == 0 && p.StartDelay == Capture.CaptureStartDelay.None;
        var inputs = settings.SavedRegions.Where(p => !(p.Id == InitialWindowKey && !p.IsDefault && Untouched(p, CaptureMode.Window, "ウィンドウ")))
            .Select(p =>
            {
                if (p.Id == StandardInputKey && Untouched(p, CaptureMode.Rectangle, "標準")) p = p with { Name = "囲った範囲" };
                return p.ProfileVersion >= 2 ? p : p with { ProfileVersion = 2, RecordSystemAudio = settings.RecordSystemAudio,
                    RecordMicrophone = settings.RecordMicrophone, MicrophoneDeviceId = settings.MicrophoneDeviceId };
            }).ToList();
        var outputs = settings.CapturePresets.Select(p => p.Id == StandardOutputKey && p.Name == "標準" &&
            p == (CapturePreset.FromSettings("標準", baseline with { SelectedImageEditorId = p.ImageEditorId ?? "", SelectedVideoEditorId = p.VideoEditorId ?? "" })
                with { Id = p.Id, IsDefault = p.IsDefault, BuiltInKey = p.BuiltInKey }) ? p with { Name = "クリップボード" } : p).ToArray();
        return settings with { PresetSchemaVersion = 3, SavedRegions = inputs.ToArray(), CapturePresets = outputs };
    }
    private static AppSettings UpgradeFixedSizes(AppSettings settings)
    {
        if (settings.PresetSchemaVersion >= 4) return settings;

        var entries = settings.SavedRegions.ToList();
        foreach (var (key, oldSize, size) in FixedTemplates)
        {
            string oldId = $"initial-input-fixed-{oldSize.Width}-{oldSize.Height}";
            var existing = entries.FirstOrDefault(preset => preset.BuiltInKey == key && IsUnedited(preset, size));
            var replace = existing is null ? entries.FirstOrDefault(preset =>
                IsLegacySlot(preset, key, oldId) && IsUnedited(preset, oldSize)) : null;
            var previous = replace ?? existing;
            for (int index = 0; index < entries.Count; index++)
            {
                SavedRegion entry = entries[index];
                if (entry != previous && IsLegacySlot(entry, key, oldId) && entry.BuiltInKey is not null)
                    entries[index] = entry with { BuiltInKey = null };
            }
            if (existing is not null) continue;
            string name = UniqueName(FixedName(size), entries.Where(entry => entry != replace).Select(entry => entry.Name));
            var next = FixedPreset(name, size) with
            {
                Id = replace?.Id ?? UniqueId(key, entries.Select(entry => entry.Id)),
                BuiltInKey = key,
                IsDefault = replace?.IsDefault == true
            };
            if (replace is not null) entries[entries.IndexOf(replace)] = next;
            else entries.Add(next);
        }

        const string old4K = "initial-input-fixed-3840-2160";
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (!IsLegacySlot(entry, old4K, old4K)) continue;
            if (entry.BuiltInKey is not null) entries[index] = entry with { BuiltInKey = null };
        }

        var initial = settings.SavedRegions.Single(preset => preset.IsDefault);
        var current = entries.Single(preset => preset.IsDefault);
        if (initial.Bounds.Size != current.Bounds.Size)
            settings = settings with { FixedWidth = current.Bounds.Width, FixedHeight = current.Bounds.Height };
        return settings with { PresetSchemaVersion = 4, SavedRegions = OrderFixedPresets(entries.ToArray()) };

        static bool IsLegacySlot(SavedRegion entry, string key, string oldId) =>
            entry.BuiltInKey == key || entry.BuiltInKey == oldId
            || entry.BuiltInKey is null && (entry.Id == key || entry.Id == oldId);
        static bool IsUnedited(SavedRegion entry, Size size) => entry.Layout.Length == 0 &&
            entry == FixedPreset(FixedName(size), size) with
            { Id = entry.Id, BuiltInKey = entry.BuiltInKey, IsDefault = entry.IsDefault, Layout = entry.Layout };
    }

    internal static SavedRegion[] OrderFixedPresets(SavedRegion[] entries)
    {
        var keys = FixedTemplates.Select(template => template.Key).ToArray();
        int[] positions = Enumerable.Range(0, entries.Length)
            .Where(index => keys.Contains(entries[index].BuiltInKey)).ToArray();
        var sorted = positions.Select(index => entries[index]).OrderBy(entry => Array.IndexOf(keys, entry.BuiltInKey)).ToArray();
        for (int index = 0; index < positions.Length; index++) entries[positions[index]] = sorted[index];
        return entries;
    }

    private static string FixedName(Size size) => $"固定サイズ {size.Width}×{size.Height}";

    private static SavedRegion FixedPreset(string name, Size size) => SavedRegion.FromSettings(name, new AppSettings
    {
        DefaultMode = CaptureMode.FixedRegion, FixedWidth = size.Width, FixedHeight = size.Height
    });

    private static AppSettings EnsureExternalApplications(AppSettings settings)
    {
        if (settings.ExternalApplicationsVersion >= 1) return settings;
        // Schema3でも旧exe欄だけを持つ設定がある。初期候補より先に登録へ移す。
        if (settings.ImageEditors.Length == 0 && !string.IsNullOrWhiteSpace(settings.ImageEditorPath))
            settings = settings with { ImageEditors = [new ExternalApplication { Id = "legacy-image",
                Name = Path.GetFileNameWithoutExtension(settings.ImageEditorPath), Executable = settings.ImageEditorPath, Arguments = settings.ImageEditorArguments }],
                SelectedImageEditorId = "legacy-image" };
        var apps = settings.ImageEditors.ToList();
        var paint = apps.FirstOrDefault(a => string.Equals(a.Executable, EditorExecutable.LegacyPaint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Executable, EditorExecutable.PaintAlias, StringComparison.OrdinalIgnoreCase));
        bool initiallyEmpty = apps.Count == 0 && string.IsNullOrWhiteSpace(settings.ImageEditorPath);
        if (paint is null && apps.Count < 50)
        {
            paint = new ExternalApplication { Id = UniqueId(PaintKey, apps.Select(a => a.Id).Concat(settings.VideoEditors.Select(a => a.Id))),
                BuiltInKey = PaintKey,
                Name = UniqueName("ペイント", apps.Select(a => a.Name)), Executable = EditorExecutable.PaintPreset };
            apps.Add(paint);
        }
        return settings with { ExternalApplicationsVersion = 1, ImageEditors = apps.ToArray(),
            SelectedImageEditorId = initiallyEmpty && paint is not null ? paint.Id : settings.SelectedImageEditorId };
    }
    private static AppSettings EnsureLegacyPresets(AppSettings settings)
    {
        if (settings.PresetSchemaVersion >= 1) return settings;
        var inputs = settings.SavedRegions.Select(r => r.ProfileVersion >= 1 ? r : r with
        { ProfileVersion = 1, FreezeFrame = settings.FreezeFrame, IncludeCursor = settings.IncludeCursor,
            PaddingPixels = settings.ScreenshotPaddingPixels }).ToList();
        var baseline = new AppSettings();
        var standard = SavedRegion.FromSettings("標準", settings) with { Id = UniqueId(StandardInputKey, inputs.Select(p => p.Id)), IsDefault = true };
        // 旧設定の個別条件を取り込んだ登録は、初期値そのものから作った候補と区別する。
        if (standard == SavedRegion.FromSettings(standard.Name, baseline) with { Id = standard.Id, IsDefault = true })
            standard = standard with { BuiltInKey = StandardInputKey };
        inputs = inputs.Select(p => p with { IsDefault = false }).Prepend(standard).ToList();
        var outputs = settings.CapturePresets.Select(p => p with { IsDefault = false, ColorDepth = settings.ScreenshotColorDepth,
            OpenFolderAfterSave = settings.OpenFolderAfterSave, OcrSeparator = settings.OcrSeparator,
            ImageEditorId = p.ImageEditorId ?? settings.SelectedImageEditorId, VideoEditorId = p.VideoEditorId ?? settings.SelectedVideoEditorId }).ToList();
        foreach (var old in outputs.Where(p => p.PaddingPixels != settings.ScreenshotPaddingPixels))
            inputs.Add(SavedRegion.FromSettings(old.Name + "（旧余白）", settings) with
            { PaddingPixels = old.PaddingPixels, Id = UniqueId("padding-" + old.Id, inputs.Select(p => p.Id)) });
        string name = "標準";
        for (int index = 2; outputs.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); index++) name = "標準 " + index;
        var standardOutput = CapturePreset.FromSettings(name, settings) with
        { IsDefault = true, Id = UniqueId(StandardOutputKey, outputs.Select(p => p.Id)) };
        string paintId = settings.ImageEditors.FirstOrDefault(app => app.BuiltInKey == PaintKey)?.Id ?? "";
        if (standardOutput == CapturePreset.FromSettings(name, baseline with { SelectedImageEditorId = paintId })
            with { Id = standardOutput.Id, IsDefault = true })
            standardOutput = standardOutput with { BuiltInKey = StandardOutputKey };
        outputs.Insert(0, standardOutput);
        return settings with { PresetSchemaVersion = 1, SavedRegions = inputs.ToArray(), CapturePresets = outputs.ToArray() };
    }
    internal static AppSettings ApplyDefaults(this AppSettings settings)
    {
        settings = settings.EnsurePresets();
        return settings.CapturePresets.Single(p => p.IsDefault).Apply(settings.SavedRegions.Single(p => p.IsDefault).Apply(settings));
    }
    internal static AppSettings UpdateDefaultProjections(this AppSettings settings)
    {
        settings = settings.EnsurePresets();
        return settings with
        {
            SavedRegions = settings.SavedRegions.Select(p => !p.IsDefault ? p : p with
            { Mode = settings.DefaultMode, Bounds = p.UseFixedCoordinates ? p.Bounds : new Rectangle(p.Bounds.Location, new(settings.FixedWidth, settings.FixedHeight)),
                FixedAnchor = settings.FixedCaptureAnchor, FreezeFrame = settings.FreezeFrame, IncludeCursor = settings.IncludeCursor,
                SelectionMethod = settings.SelectionMethod,
                RecordSystemAudio = settings.RecordSystemAudio, RecordMicrophone = settings.RecordMicrophone, MicrophoneDeviceId = settings.MicrophoneDeviceId }).ToArray(),
            CapturePresets = settings.CapturePresets.Select(p => !p.IsDefault ? p : CapturePreset.FromSettings(p.Name, settings) with
            { Id = p.Id, IsDefault = true, BuiltInKey = p.BuiltInKey, PaddingPixels = p.PaddingPixels, WhitePaddingPixels = p.WhitePaddingPixels }).ToArray()
        };
    }
    internal static string UniqueId(string seed, IEnumerable<string> ids)
    {
        var used = ids.ToHashSet(StringComparer.Ordinal); string result = seed;
        for (int index = 2; used.Contains(result); index++) result = seed + "-" + index;
        return result;
    }
    internal static string UniqueName(string seed, IEnumerable<string> names)
    {
        var used = names.ToHashSet(StringComparer.OrdinalIgnoreCase); string result = seed;
        for (int index = 2; used.Contains(result); index++) result = seed + " " + index;
        return result;
    }
}
