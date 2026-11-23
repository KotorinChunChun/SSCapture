using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSCapture.Services;

internal sealed class SettingsStore(string path)
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<CaptureMode>(), new JsonStringEnumConverter<OutputDestination>(),
            new JsonStringEnumConverter<CaptureIntent>(), new JsonStringEnumConverter<NotificationCorner>(), new JsonStringEnumConverter<NotificationDisplay>(),
            new JsonStringEnumConverter<SelectionGuideDisplay>(),
            new JsonStringEnumConverter<CaptureAnchor>(), new JsonStringEnumConverter<Capture.CaptureKeyAction>(),
            new JsonStringEnumConverter<Capture.CaptureClickAction>(), new JsonStringEnumConverter<Capture.CaptureAnchorCycle>(), new JsonStringEnumConverter<Capture.CaptureSelectionMethod>(), new JsonStringEnumConverter<Capture.CaptureStartDelay>(),
            new JsonStringEnumConverter<Capture.CaptureActivation>() }
    };
    private static readonly JsonSerializerOptions StrictReadOptions = new(Options) { RespectNullableAnnotations = true };
    private readonly string filePath = Path.GetFullPath(path);

    public AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath, Encoding.UTF8));
            return ReadSettings(document.RootElement);
        }
        catch (FileNotFoundException) { return new AppSettings().ApplyDefaults(); }
        catch (DirectoryNotFoundException) { return new AppSettings().ApplyDefaults(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or FormatException or InvalidOperationException)
        {
            warning = $"設定を読み込めないため既定値で起動しました。元の設定ファイルは保持されています。{Environment.NewLine}{ex.Message}";
            return new AppSettings().ApplyDefaults();
        }
    }

    internal static AppSettings ReadSettings(JsonElement root, bool strict = false)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("設定はJSONオブジェクトで指定してください。");
        AppSettings settings = root.Deserialize<AppSettings>(strict ? StrictReadOptions : Options) ?? throw new JsonException("設定が空です。");
        if (root.TryGetProperty(nameof(AppSettings.PresetCaptureShortcuts), out _) && settings.PresetCaptureShortcuts is null)
            throw new JsonException("プリセット撮影の一覧を指定してください。");
        // 旧設定で省略されていた再撮影キーは、当時の未割り当てを維持する。
        if (!root.TryGetProperty(nameof(AppSettings.LastRecipeShortcut), out _))
            settings = settings with { LastRecipeShortcut = "" };
        // 旧設定で省略されていた暗転は、当時の初期値OFFを維持する。
        if (!root.TryGetProperty(nameof(AppSettings.DimCaptureBackground), out _))
            settings = settings with { DimCaptureBackground = false };
        if (!root.TryGetProperty(nameof(AppSettings.NotificationDurationSeconds), out _)
            && root.TryGetProperty(nameof(AppSettings.ShowCaptureNotification), out var showNotification)
            && showNotification.ValueKind == JsonValueKind.False)
            settings = settings with { NotificationDurationSeconds = 0 };
        if (!root.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() < 2)
        {
            if (root.TryGetProperty("DefaultDestination", out var destination) && destination.ValueKind == JsonValueKind.Number)
                settings = settings with { DefaultDestination = destination.GetInt32() switch
                {
                    0 => OutputDestination.Clipboard, 1 => OutputDestination.File, 2 => OutputDestination.ExternalApp,
                    _ => throw new JsonException("旧設定の送信先が正しくありません。")
                } };
            if (root.TryGetProperty("ShortcutEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                settings = settings with { ScreenshotShortcut1 = "", ScreenshotShortcut2 = "", RecordingShortcut1 = "", RecordingShortcut2 = "", InstantShortcuts = Enumerable.Repeat("", 10).ToArray() };
            settings = settings with { SchemaVersion = 2 };
        }
        if (!root.TryGetProperty(nameof(AppSettings.InstantShortcuts), out _))
        {
            // Preserve existing custom keys when adding the new default slots.
            var keys = PresetCaptureShortcut.LegacySlots(settings).Select(slot => (string?)slot.Key)
                .Concat(settings.PresetCaptureShortcuts is { } rows ? rows.Select(row => row?.Key)
                    : settings.SavedRegions?.Select(region => region?.Shortcut) ?? [])
                .Append(settings.LastRecipeShortcut);
            var existing = keys.Select(HotkeyBinding.Parse).Where(binding => binding is not null).ToHashSet();
            settings = settings with { InstantShortcuts = settings.InstantShortcuts.Select(key => existing.Contains(HotkeyBinding.Parse(key)) ? "" : key).ToArray() };
        }
        if (!root.TryGetProperty("SchemaVersion", out var editorVersion) || editorVersion.GetInt32() < 3)
            settings = settings.MigrateEditors();
        if (root.TryGetProperty(nameof(AppSettings.CaptureMouse), out var mouse) && mouse.ValueKind == JsonValueKind.Object)
        {
            var map = settings.CaptureMouse;
            settings = settings with { CaptureMouse = map with
            {
                BeforeLeftAction = ReadLegacyClick("BeforeLeft", nameof(map.BeforeLeftAction), map.BeforeLeftAction, Capture.CaptureClickAction.Select),
                BeforeRightAction = ReadLegacyClick("BeforeRight", nameof(map.BeforeRightAction), map.BeforeRightAction, Capture.CaptureClickAction.Menu),
                BeforeMiddleAction = ReadLegacyClick("BeforeMiddle", nameof(map.BeforeMiddleAction), map.BeforeMiddleAction, Capture.CaptureClickAction.Desktop),
                DragRightAction = ReadLegacyClick("DragRight", nameof(map.DragRightAction), map.DragRightAction, Capture.CaptureClickAction.Cancel)
            } };

            Capture.CaptureClickAction ReadLegacyClick(string oldName, string newName, Capture.CaptureClickAction fallback,
                Capture.CaptureClickAction enabled) => !mouse.TryGetProperty(newName, out _) && mouse.TryGetProperty(oldName, out var flag)
                ? flag.GetBoolean() ? enabled : Capture.CaptureClickAction.None : fallback;
        }
        settings = RemoveRetiredEditor(settings);
        settings.Validate();
        settings = settings with
        {
            CaptureBeforeKeys = settings.CaptureBeforeKeys.WithBeforeActivations(settings.ControlActivation, settings.WindowActivation)
        };
        settings = settings.ApplyDefaults();
        settings.Validate();
        return settings;
    }

    private static AppSettings RemoveRetiredEditor(AppSettings settings)
    {
        // 旧開発版がJSONに保存した専用起動方式の値。
        const ExternalApplicationLaunch retiredLaunch = (ExternalApplicationLaunch)2;
        if (settings.ImageEditors is not { Length: > 0 } || settings.VideoEditors is null || settings.CapturePresets is null)
            return settings;
        var retiredIds = settings.ImageEditors.Where(app => app?.Launch == retiredLaunch)
            .Select(app => app.Id).ToHashSet(StringComparer.Ordinal);
        if (retiredIds.Count == 0) return settings;

        bool selected = retiredIds.Contains(settings.SelectedImageEditorId);
        bool notification = retiredIds.Contains(settings.NotificationApplicationId)
            && !settings.VideoEditors.Any(app => app?.Id == settings.NotificationApplicationId);
        return settings with
        {
            ImageEditors = settings.ImageEditors.Where(app => app?.Launch != retiredLaunch).ToArray(),
            SelectedImageEditorId = selected ? "" : settings.SelectedImageEditorId,
            ImageEditorPath = selected ? "" : settings.ImageEditorPath,
            ImageEditorArguments = selected ? EditorArguments.Default : settings.ImageEditorArguments,
            DefaultDestination = selected ? settings.DefaultDestination & ~OutputDestination.ExternalApp : settings.DefaultDestination,
            NotificationApplicationId = notification ? "" : settings.NotificationApplicationId,
            NotificationClickAction = notification && settings.NotificationClickAction == NotificationClickAction.ExternalApplication
                ? NotificationClickAction.None : settings.NotificationClickAction,
            CapturePresets = settings.CapturePresets.Select(preset => preset is { ImageEditorId: string id } && retiredIds.Contains(id)
                ? preset with { ImageEditorId = null, Destination = preset.Destination & ~OutputDestination.ExternalApp }
                : preset).ToArray()
        };
    }

    public void Save(AppSettings settings) => Save(settings, import: false);

    internal void Import(AppSettings settings) => Save(settings, import: true);

    internal T PrepareAndSave<T>(AppSettings settings, bool import, Func<T> prepare) where T : IDisposable
    {
        T prepared = prepare();
        try
        {
            Save(settings, import);
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    private void Save(AppSettings settings, bool import)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.UpdateDefaultProjections();
        settings.Validate();
        if (File.Exists(filePath))
        {
            if (import)
            {
                AtomicFile.Write(filePath + ".import.bak", destination =>
                {
                    using var previous = File.OpenRead(filePath);
                    previous.CopyTo(destination);
                });
            }
            else BackupLegacySettings();
        }
        AtomicFile.Write(filePath, stream => JsonSerializer.Serialize(stream, settings, Options));
    }

    private void BackupLegacySettings()
    {
        string? suffix = null;
        try
        {
            using var old = JsonDocument.Parse(File.ReadAllText(filePath, Encoding.UTF8));
            if (!old.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() < 3) suffix = ".schema2.bak";
            else if (!old.RootElement.TryGetProperty(nameof(AppSettings.PresetSchemaVersion), out var profiles) || profiles.GetInt32() < 1) suffix = ".presets-v0.bak";
            else if (profiles.GetInt32() < 2) suffix = ".presets-v1.bak";
            else if (profiles.GetInt32() < 3) suffix = ".presets-v2.bak";
            else if (!old.RootElement.TryGetProperty(nameof(AppSettings.ExternalApplicationsVersion), out var apps) || apps.GetInt32() < 1) suffix = ".external-apps-v0.bak";
            else if (profiles.GetInt32() < 4) suffix = ".presets-v3.bak";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { suffix = ".invalid.bak"; }
        if (suffix is not null && !File.Exists(filePath + suffix)) File.Copy(filePath, filePath + suffix, false);
    }
}
