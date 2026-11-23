using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json;
using System.Text.Json.Serialization;
using SSCapture.Capture;
using SSCapture.Recording;

namespace SSCapture.Services;

internal static class SettingsTransfer
{
    private const string Format = "SSCapture.Settings";
    private const int FormatVersion = 1;
    private static readonly AppSettings CurrentVersions = new AppSettings().ApplyDefaults();
    private static readonly HashSet<string> SettingsProperties = typeof(AppSettings).GetProperties()
        .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
        .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions ExportOptions = new(SettingsStore.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static AppSettings Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8), new JsonDocumentOptions { AllowDuplicateProperties = false });
        return Parse(document.RootElement);
    }

    internal static AppSettings Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("設定はJSONオブジェクトで指定してください。");
        if (root.TryGetProperty("Format", out var format) || root.TryGetProperty("FormatVersion", out _) || root.TryGetProperty("Settings", out _))
        {
            if (format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw new JsonException("SSCaptureの設定形式ではありません。");
            if (!root.TryGetProperty("FormatVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int value) || value != FormatVersion)
                throw new JsonException("未対応の設定ファイル形式バージョンです。");
            if (!root.TryGetProperty("Settings", out var settingsNode) || settingsNode.ValueKind != JsonValueKind.Object)
                throw new JsonException("設定の内容がありません。");
            root = settingsNode;
        }

        if (!root.EnumerateObject().Any(property => SettingsProperties.Contains(property.Name)
            || property.Name is "ShortcutEnabled" or nameof(AppSettings.ShowCaptureNotification)))
            throw new JsonException("SSCaptureの設定項目が見つかりません。");
        CheckVersion(root, nameof(AppSettings.SchemaVersion), CurrentVersions.SchemaVersion);
        CheckVersion(root, nameof(AppSettings.PresetSchemaVersion), CurrentVersions.PresetSchemaVersion);
        CheckVersion(root, nameof(AppSettings.ExternalApplicationsVersion), CurrentVersions.ExternalApplicationsVersion);

        AppSettings settings = SettingsStore.ReadSettings(root, strict: true).UpdateDefaultProjections();
        settings.Validate();
        return settings;
    }

    private static void CheckVersion(JsonElement root, string name, int maximum)
    {
        if (root.TryGetProperty(name, out var field) && (field.ValueKind != JsonValueKind.Number
            || !field.TryGetInt32(out int version) || version < 0 || version > maximum))
            throw new JsonException($"未対応の設定バージョンです（{name}）。");
    }

    internal static void Export(string path, AppSettings settings)
    {
        settings = settings.ApplyDefaults();
        settings.Validate();
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream,
            new { Format, FormatVersion, Settings = settings }, ExportOptions), overwrite: false);
    }

    internal static string Preview(AppSettings current, AppSettings candidate,
        Func<IReadOnlyList<DisplayLayout>> readDisplays, Func<IReadOnlyList<MicrophoneInfo>> readMicrophones,
        Func<IReadOnlyList<DisplayTarget>>? readDisplayTargets = null)
    {
        var candidateKeys = HotkeyBinding.ReadAssignments(candidate);
        var preview = new StringBuilder()
            .AppendLine($"入力プリセット {current.SavedRegions.Length} → {candidate.SavedRegions.Length}件")
            .AppendLine($"出力プリセット {current.CapturePresets.Length} → {candidate.CapturePresets.Length}件")
            .AppendLine($"撮影・録画ショートカット {HotkeyBinding.ReadAssignments(current).Length} → {candidateKeys.Length}件")
            .AppendLine($"外部アプリ {current.ImageEditors.Length + current.VideoEditors.Length} → {candidate.ImageEditors.Length + candidate.VideoEditors.Length}件")
            .AppendLine($"手動起動時の管理者実行 {(current.RunAsAdministrator ? "ON" : "OFF")} → {(candidate.RunAsAdministrator ? "ON" : "OFF")}")
            .AppendLine($"撮影履歴の保持 {(current.KeepHistory ? "ON" : "OFF")} → {(candidate.KeepHistory ? "ON" : "OFF")}")
            .AppendLine($"履歴の保持日数 {current.HistoryRetentionDays} → {candidate.HistoryRetentionDays}（0は期限なし）")
            .AppendLine($"再起動後の履歴 {(current.PersistHistory ? "ON" : "OFF")} → {(candidate.PersistHistory ? "ON" : "OFF")}");
        if (current.KeepHistory && !candidate.KeepHistory)
            preview.AppendLine("適用すると、保持中の撮影履歴が消去されます。");
        else if (current.PersistHistory && !candidate.PersistHistory)
            preview.AppendLine("適用すると、ディスク上の履歴用画像が消去されます。");
        foreach (var app in candidate.ImageEditors.Concat(candidate.VideoEditors))
        {
            bool executable = app.Launch == ExternalApplicationLaunch.Executable;
            bool available = executable ? IsEditorAvailable(app.Executable) : new ExternalEditor().CanOpen(app);
            preview.AppendLine($"外部アプリ「{app.Name}」 [{(available ? "確認済み" : "未確認・外部アプリで再指定")}]");
            if (executable) preview.AppendLine($"  実行先  {app.Executable}").AppendLine($"  引数  {app.Arguments}");
            else preview.AppendLine("  起動方法  PNGの関連付け");
        }

        foreach (var preset in candidate.CapturePresets)
            AppendDirectory($"出力プリセット「{preset.Name}」の保存先", preset.SaveDirectory);
        AppendDirectory("出力プリセット共通のOneOCRフォルダ", candidate.OneOcrDirectory, "自動検出");

        if (candidate.SavedRegions.Any(region => region.UseFixedCoordinates))
        {
            var displays = ReadDevices(readDisplays);
            foreach (var region in candidate.SavedRegions.Where(region => region.UseFixedCoordinates))
            {
                try
                {
                    if (displays is null) throw new InvalidOperationException("画面構成を取得できません。");
                    _ = RegionCoordinates.Resolve(region, displays);
                    preview.AppendLine($"入力プリセット「{region.Name}」のモニター [確認済み]");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    preview.AppendLine($"入力プリセット「{region.Name}」のモニター [未確認・範囲を再登録] {ex.Message}");
                }
            }
        }

        var instantSlots = candidateKeys.Where(assignment => assignment.DisplayNumber > 0).Select(assignment => assignment.DisplayNumber).ToArray();
        if (instantSlots.Length > 0 || candidate.NotificationDisplay >= NotificationDisplay.Display1)
        {
            var targets = ReadDevices(readDisplayTargets ?? InstantCapture.ReadDisplays);
            int connected = targets is null ? 0 : InstantCapture.OrderDisplays(targets).Count;
            if (instantSlots.Length > 0)
            {
                string attached = string.Join("、", instantSlots.Where(slot => slot <= connected).Select(slot => $"画面{slot}"));
                string missing = string.Join("、", instantSlots.Where(slot => slot > connected).Select(slot => $"画面{slot}"));
                if (attached.Length > 0) preview.AppendLine($"即時撮影キーのモニター [確認済み] {attached}");
                if (missing.Length > 0) preview.AppendLine($"即時撮影キーのモニター [接続未確認] {missing}");
            }
            if (candidate.NotificationDisplay >= NotificationDisplay.Display1)
            {
                int slot = (int)candidate.NotificationDisplay - (int)NotificationDisplay.Display1 + 1;
                bool attached = targets?.Any(target => string.Equals(target.DeviceName,
                    @"\\.\DISPLAY" + slot, StringComparison.OrdinalIgnoreCase)) == true;
                preview.AppendLine($"通知先のモニター 画面{slot} [{(attached ? "確認済み" : "未接続・通知で再指定")}]");
            }
        }

        if (candidate.SavedRegions.Any(region => region.RecordMicrophone || region.MicrophoneDeviceId.Length > 0))
        {
            var microphones = ReadDevices(readMicrophones);
            foreach (var region in candidate.SavedRegions.Where(region => region.RecordMicrophone || region.MicrophoneDeviceId.Length > 0))
            {
                var microphone = microphones?.FirstOrDefault(item => item.Id == region.MicrophoneDeviceId);
                string defaultDevice = microphones?.Count > 0 ? "既定のマイク" : "既定のマイク・接続未確認";
                string state = microphone?.Name ?? (region.MicrophoneDeviceId.Length == 0
                    ? defaultDevice : "未接続・入力プリセットで再指定");
                preview.AppendLine($"入力プリセット「{region.Name}」のマイク [{state}]");
            }
        }
        return preview.ToString();

        void AppendDirectory(string label, string? path, string empty = "未指定")
        {
            if (string.IsNullOrWhiteSpace(path)) preview.AppendLine($"{label} [{empty}]");
            else
            {
                bool exists = IsLocalPath(path) && Directory.Exists(path);
                preview.AppendLine($"{label} [{(exists ? "確認済み" : "未確認・出力プリセットで再指定")}]{Environment.NewLine}  {path}");
            }
        }
    }

    private static IReadOnlyList<T>? ReadDevices<T>(Func<IReadOnlyList<T>> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { return null; }
    }

    private static bool IsEditorAvailable(string path)
    {
        if (!IsLocalPath(path)) return false;
        try { return EditorExecutable.Resolve(path, candidate => IsLocalPath(candidate) && File.Exists(candidate)).Length > 0; }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
}
