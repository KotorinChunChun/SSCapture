namespace SSCapture.Services;

internal static class PresetSettings
{
    internal static AppSettings EnsurePresets(this AppSettings settings)
    {
        settings = EnsureExternalApplications(settings);
        if (settings.PresetSchemaVersion >= 2) return UpgradePresets(settings);
        settings = EnsureLegacyPresets(settings);
        var inputs = settings.SavedRegions.ToList();
        var outputs = settings.CapturePresets.ToList();
        var baseline = new AppSettings();
        foreach (var (name, id, mode) in new[]
        {
            ("ウィンドウ", "initial-input-window", CaptureMode.Window),
            ("固定サイズ 1280×720", "initial-input-fixed", CaptureMode.FixedRegion),
            ("矩形選択", "initial-input-rectangle", CaptureMode.Rectangle)
        })
        {
            if (inputs.Count >= 3) break;
            if (inputs.Any(p => !p.UseFixedCoordinates && p.Mode == mode)) continue;
            inputs.Add(SavedRegion.FromSettings(UniqueName(name, inputs.Select(p => p.Name)), baseline with { DefaultMode = mode })
                with { Id = UniqueId(id, inputs.Select(p => p.Id)) });
        }
        foreach (var (name, id, destination) in new[]
        {
            ("ファイル保存", "initial-output-file", OutputDestination.File),
            ("クリップボード＋ファイル", "initial-output-both", OutputDestination.Clipboard | OutputDestination.File),
            ("クリップボード", "initial-output-clipboard", OutputDestination.Clipboard)
        })
        {
            if (outputs.Count >= 3) break;
            if (outputs.Any(p => p.Destination == destination)) continue;
            outputs.Add(CapturePreset.FromSettings(UniqueName(name, outputs.Select(p => p.Name)), baseline with { DefaultDestination = destination })
                with { Id = UniqueId(id, outputs.Select(p => p.Id)) });
        }
        // 削除した初期候補を次の起動で復活させない。既定や既存の登録内容には触れない。
        return UpgradePresets(settings with { PresetSchemaVersion = 2, SavedRegions = inputs.ToArray(), CapturePresets = outputs.ToArray() });
    }
    private static AppSettings UpgradePresets(AppSettings settings)
    {
        if (settings.PresetSchemaVersion >= 3) return settings;
        var baseline = new AppSettings();
        bool Untouched(SavedRegion p, CaptureMode mode, string name) => p.Name == name && !p.UseFixedCoordinates && p.Mode == mode &&
            p.Bounds == new Rectangle(0, 0, baseline.FixedWidth, baseline.FixedHeight) && p.Shortcut == "" &&
            p.FixedAnchor == CaptureAnchor.TopLeft && p.FreezeFrame && !p.IncludeCursor && p.PaddingPixels == 0;
        var inputs = settings.SavedRegions.Where(p => !(p.Id == "initial-input-window" && !p.IsDefault && Untouched(p, CaptureMode.Window, "ウィンドウ")))
            .Select(p =>
            {
                if (p.Id == "standard-input" && Untouched(p, CaptureMode.Rectangle, "標準")) p = p with { Name = "囲った範囲" };
                return p.ProfileVersion >= 2 ? p : p with { ProfileVersion = 2, RecordSystemAudio = settings.RecordSystemAudio,
                    RecordMicrophone = settings.RecordMicrophone, MicrophoneDeviceId = settings.MicrophoneDeviceId };
            }).ToList();
        foreach (Size size in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
        {
            if (inputs.Count >= 50 || inputs.Any(p => !p.UseFixedCoordinates && p.Mode == CaptureMode.FixedRegion && p.Bounds.Size == size)) continue;
            inputs.Add(SavedRegion.FromSettings(UniqueName($"固定サイズ {size.Width}×{size.Height}", inputs.Select(p => p.Name)),
                baseline with { DefaultMode = CaptureMode.FixedRegion, FixedWidth = size.Width, FixedHeight = size.Height }) with
                { Id = UniqueId($"initial-input-fixed-{size.Width}-{size.Height}", inputs.Select(p => p.Id)) });
        }
        var outputs = settings.CapturePresets.Select(p => p.Id == "standard-output" && p.Name == "標準" &&
            p == (CapturePreset.FromSettings("標準", baseline with { SelectedImageEditorId = p.ImageEditorId ?? "", SelectedVideoEditorId = p.VideoEditorId ?? "" })
                with { Id = p.Id, IsDefault = p.IsDefault }) ? p with { Name = "クリップボード" } : p).ToArray();
        return settings with { PresetSchemaVersion = 3, SavedRegions = inputs.ToArray(), CapturePresets = outputs };
    }
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
            paint = new ExternalApplication { Id = UniqueId("builtin-paint", apps.Select(a => a.Id)),
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
        var standard = SavedRegion.FromSettings("標準", settings) with { Id = UniqueId("standard-input", inputs.Select(p => p.Id)), IsDefault = true };
        inputs = inputs.Select(p => p with { IsDefault = false }).Prepend(standard).ToList();
        var outputs = settings.CapturePresets.Select(p => p with { IsDefault = false, ColorDepth = settings.ScreenshotColorDepth,
            OpenFolderAfterSave = settings.OpenFolderAfterSave, OcrSeparator = settings.OcrSeparator,
            ImageEditorId = p.ImageEditorId ?? settings.SelectedImageEditorId, VideoEditorId = p.VideoEditorId ?? settings.SelectedVideoEditorId }).ToList();
        foreach (var old in outputs.Where(p => p.PaddingPixels != settings.ScreenshotPaddingPixels))
            inputs.Add(SavedRegion.FromSettings(old.Name + "（旧余白）", settings) with
            { PaddingPixels = old.PaddingPixels, Id = UniqueId("padding-" + old.Id, inputs.Select(p => p.Id)) });
        string name = "標準";
        for (int index = 2; outputs.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); index++) name = "標準 " + index;
        outputs.Insert(0, CapturePreset.FromSettings(name, settings) with { IsDefault = true, Id = UniqueId("standard-output", outputs.Select(p => p.Id)) });
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
                FixedAnchor = settings.FixedCaptureAnchor, FreezeFrame = settings.FreezeFrame, IncludeCursor = settings.IncludeCursor, PaddingPixels = settings.ScreenshotPaddingPixels,
                RecordSystemAudio = settings.RecordSystemAudio, RecordMicrophone = settings.RecordMicrophone, MicrophoneDeviceId = settings.MicrophoneDeviceId }).ToArray(),
            CapturePresets = settings.CapturePresets.Select(p => !p.IsDefault ? p : CapturePreset.FromSettings(p.Name, settings) with { Id = p.Id, IsDefault = true }).ToArray()
        };
    }
    private static string UniqueId(string seed, IEnumerable<string> ids)
    {
        var used = ids.ToHashSet(StringComparer.Ordinal); string result = seed;
        for (int index = 2; used.Contains(result); index++) result = seed + "-" + index;
        return result;
    }
    private static string UniqueName(string seed, IEnumerable<string> names)
    {
        var used = names.ToHashSet(StringComparer.OrdinalIgnoreCase); string result = seed;
        for (int index = 2; used.Contains(result); index++) result = seed + " " + index;
        return result;
    }
}
