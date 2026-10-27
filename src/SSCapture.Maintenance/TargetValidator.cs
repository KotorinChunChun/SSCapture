using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;
using KotorinToys;

namespace SSCapture.Maintenance;

internal sealed record Registration(string Directory, string Name, string Version, string Uninstall);
internal sealed record ExecutableIdentity(string Product, string Version, string FileVersion, string AssemblyVersion);
internal sealed record ValidatedTarget(string Root, string Version, bool Legacy, bool Fresh, PayloadManifest? Manifest);

internal sealed class TargetValidator(
    Func<Registration?>? readRegistration = null,
    Func<string, ExecutableIdentity>? readExecutable = null,
    PayloadManifest? legacy = null)
{
    public bool UsesSystemRegistration => readRegistration is null;
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.SSCapture_is1";
    public static readonly ToolDefinition Definition = new("ss-capture", "SSCapture", "SSCapture.exe", "README.md",
        AppId: "KotorinToys.SSCapture", InstalledExecutable: "SSCapture.exe");
    // 0.1.0公式setupの配布物。0.1.1はこれらを上書きせず残す場合がある。
    static readonly PayloadFile[] Legacy010Documents =
    [
        new("README.md", 2656, "e4c3a54f8f20deea17cb11a23e4343397e6242b37e6bf7e05091598b723aaa08"),
        new("docs/usage.md", 23987, "dbd4fcb8eb496860c111561d252ff52be820f57dd9d177e6c70f48c97fe4f585")
    ];
    public static Registration? ReadRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key is null ? null : new(key.GetValue("InstallLocation") as string ?? "", key.GetValue("DisplayName") as string ?? "",
            key.GetValue("DisplayVersion") as string ?? "", key.GetValue("UninstallString") as string ?? "");
    }
    public static ExecutableIdentity ReadExecutable(string root)
    {
        var info = FileVersionInfo.GetVersionInfo(SafeFiles.Resolve(root, "SSCapture.exe"));
        return new(info.ProductName ?? "", info.ProductVersion ?? "", info.FileVersion ?? "",
            AssemblyName.GetAssemblyName(SafeFiles.Resolve(root, "SSCapture.dll")).Version?.ToString() ?? "");
    }
    public Registration? CurrentRegistration() => (readRegistration ?? ReadRegistration)();
    public static bool IsProductRegistrationName(string name, string version) =>
        name == "SSCapture" || name == "SSCapture " + version ||
        (version is "0.1.1" or "0.1.2") && name == "SSCapture バージョン " + version;
    public ValidatedTarget Validate(string path, bool allowLegacy, bool allowFresh)
    {
        var root = SafeFiles.Root(path);
        var registration = CurrentRegistration();
        if (registration is null)
        {
            if (!allowFresh || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("正式登録がない既存配置へ上書きしません。");
            return new(root, "", false, true, null);
        }
        if (!string.Equals(SafeFiles.Root(registration.Directory), root, StringComparison.OrdinalIgnoreCase) ||
            !IsProductRegistrationName(registration.Name, registration.Version) || !PayloadManifest.VersionNumber(registration.Version))
            throw new IOException("正式登録・製品名・導入先が一致しません。");
        var uninstaller = SafeFiles.Resolve(root, "unins000.exe");
        if (!string.Equals(registration.Uninstall.Trim().Trim('"'), uninstaller, StringComparison.OrdinalIgnoreCase) || !File.Exists(uninstaller))
            throw new IOException("削除登録が対象製品の導入先と一致しません。");
        var identity = (readExecutable ?? ReadExecutable)(root);
        // 旧setupが登録を0.1.0のまま残し、0.1.1の実行物だけ置いた既知の組合せ。
        var knownMixedLegacy = registration.Version == "0.1.0" && identity.Version == "0.1.1";
        var executableVersion = knownMixedLegacy ? "0.1.1" : registration.Version;
        if (identity.Product != "SSCapture" || identity.Version != executableVersion ||
            identity.FileVersion != executableVersion + ".0" || identity.AssemblyVersion != executableVersion + ".0" ||
            knownMixedLegacy && !allowLegacy)
            throw new IOException("実EXEの製品名・版が正式登録と一致しません。");
        var definitionPath = SafeFiles.Resolve(root, "tool.json");
        if (!File.Exists(definitionPath))
        {
            if (knownMixedLegacy) throw new IOException("旧版の製品定義がありません。登録と実EXEの不一致を修復しません。");
            return ValidateLegacy(root, registration.Version, registration.Version, allowLegacy, null);
        }
        if (new FileInfo(definitionPath).Length > 8 * 1024 * 1024) throw new IOException("製品定義の容量上限を超えています。");
        // 旧版のBOM付きUTF-8やUTF-16は本体の導入検出と同じ方法で読む。
        using var document = JsonDocument.Parse(File.ReadAllText(definitionPath));
        var json = document.RootElement;
        var manifestPath = SafeFiles.Resolve(root, "payload-manifest.json");
        if (knownMixedLegacy && File.Exists(manifestPath))
            throw new IOException("既知旧版と異なる目録があるため、登録と実EXEの不一致を修復しません。");
        var definitionVersion = json.GetProperty("version").GetString();
        // 0.1.1のsetupはtool.jsonを同梱しないため、0.1.0の定義が残ることがある。
        var knownStaleDefinition = registration.Version == "0.1.1" && definitionVersion == "0.1.0" && !File.Exists(manifestPath);
        if (json.GetProperty("schemaVersion").GetInt32() != 1 || json.GetProperty("id").GetString() != "ss-capture" ||
            json.GetProperty("name").GetString() != "SSCapture" || json.GetProperty("appId").GetString() != "KotorinToys.SSCapture" ||
            (definitionVersion != registration.Version && !knownStaleDefinition) || json.GetProperty("installedExecutable").GetString() != "SSCapture.exe")
            throw new IOException("製品定義の識別が一致しません。");
        if (!File.Exists(manifestPath) && (registration.Version == "0.1.1" || knownMixedLegacy))
            return ValidateLegacy(root, registration.Version, executableVersion, allowLegacy, definitionPath);
        var manifest = PayloadManifest.Read(manifestPath);
        if (manifest.Version != registration.Version) throw new IOException("導入済み目録の版が一致しません。");
        manifest.Verify(root);
        return new(root, registration.Version, false, false, manifest);
    }

    ValidatedTarget ValidateLegacy(string root, string registeredVersion, string payloadVersion, bool allowLegacy, string? definitionPath)
    {
        if (!allowLegacy || payloadVersion is not ("0.1.1" or "0.1.2") ||
            registeredVersion != payloadVersion && !(registeredVersion == "0.1.0" && payloadVersion == "0.1.1" && definitionPath != null))
            throw new IOException("製品定義または目録がありません。既知旧版以外は修復しません。");
        var old = legacy ?? PayloadManifest.Legacy(payloadVersion);
        if (old.Version != payloadVersion) throw new IOException("旧版の配布目録が実EXEの版と一致しません。");
        old.Verify(root);
        if (payloadVersion == "0.1.1")
        {
            var leftovers = Legacy010Documents.Where(file => File.Exists(SafeFiles.Resolve(root, file.Path))).ToArray();
            // 既知の配布物のみ所有ファイルとして扱い、編集済みの文書は上書きしない。
            if (leftovers.Length > 0)
            {
                (old with { Files = leftovers }).Verify(root);
                old = old with { Files = [.. old.Files, .. leftovers] };
            }
        }
        if (definitionPath != null)
        {
            // 旧公式配布物に含まれない定義は、識別を検証したうえで退避対象にする。
            var definition = new PayloadFile("tool.json", new FileInfo(definitionPath).Length, SafeFiles.Hash(definitionPath));
            old = old with { Files = [.. old.Files, definition] };
        }
        return new(root, registeredVersion, true, false, old);
    }
}
