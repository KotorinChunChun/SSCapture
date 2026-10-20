using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSCapture.Services;

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<CaptureMode>(), new JsonStringEnumConverter<OutputDestination>(),
            new JsonStringEnumConverter<NotificationCorner>(), new JsonStringEnumConverter<NotificationDisplay>(),
            new JsonStringEnumConverter<CaptureAnchor>() }
    };
    private readonly string filePath = Path.GetFullPath(path);

    public AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, Options)
                ?? throw new JsonException("設定が空です。");
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.NotificationDurationSeconds), out _)
                && document.RootElement.TryGetProperty(nameof(AppSettings.ShowCaptureNotification), out var showNotification)
                && showNotification.ValueKind == JsonValueKind.False)
                settings = settings with { NotificationDurationSeconds = 0 };
            if (!document.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() < 2)
            {
                if (document.RootElement.TryGetProperty("DefaultDestination", out var destination) && destination.ValueKind == JsonValueKind.Number)
                    settings = settings with { DefaultDestination = destination.GetInt32() switch
                    {
                        0 => OutputDestination.Clipboard, 1 => OutputDestination.File, 2 => OutputDestination.ExternalApp,
                        _ => throw new JsonException("旧設定の送信先が正しくありません。")
                    } };
                if (document.RootElement.TryGetProperty("ShortcutEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                    settings = settings with { ScreenshotShortcut1 = "", ScreenshotShortcut2 = "", RecordingShortcut1 = "", RecordingShortcut2 = "", InstantShortcuts = Enumerable.Repeat("", 10).ToArray(), LastRegionShortcut = "" };
                settings = settings with { SchemaVersion = 2 };
            }
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.InstantShortcuts), out _))
            {
                // Preserve existing custom keys when adding the new default slots.
                var existing = new[] { settings.ScreenshotShortcut1, settings.ScreenshotShortcut2, settings.RecordingShortcut1, settings.RecordingShortcut2 }
                    .Select(HotkeyBinding.Parse).Where(binding => binding is not null).ToHashSet();
                settings = settings with { InstantShortcuts = settings.InstantShortcuts.Select(key => existing.Contains(HotkeyBinding.Parse(key)) ? "" : key).ToArray() };
            }
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.LastRegionShortcut), out _))
            {
                var assigned = HotkeyBinding.ReadAssignments(settings with { LastRegionShortcut = "" });
                if (HotkeyBinding.Parse(settings.LastRegionShortcut) is { } repeat && assigned.Any(a => a.Binding == repeat))
                    settings = settings with { LastRegionShortcut = "" };
            }
            if (!document.RootElement.TryGetProperty("SchemaVersion", out var editorVersion) || editorVersion.GetInt32() < 3)
                settings = settings.MigrateEditors();
            settings.Validate();
            return settings;
        }
        catch (FileNotFoundException) { return new AppSettings(); }
        catch (DirectoryNotFoundException) { return new AppSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            warning = $"設定を読み込めないため既定値で起動しました。元の設定ファイルは保持されています。{Environment.NewLine}{ex.Message}";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (File.Exists(filePath))
        {
            string? suffix = null;
            try
            {
                using var old = JsonDocument.Parse(File.ReadAllText(filePath, Encoding.UTF8));
                if (!old.RootElement.TryGetProperty("SchemaVersion", out var version) || version.GetInt32() < 3) suffix = ".schema2.bak";
            }
            catch (JsonException) { suffix = ".invalid.bak"; }
            if (suffix is not null && !File.Exists(filePath + suffix)) File.Copy(filePath, filePath + suffix, false);
        }
        AtomicFile.Write(filePath, stream => JsonSerializer.Serialize(stream, settings, Options));
    }
}
