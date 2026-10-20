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
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.SSCapture_is1";
    public static readonly ToolDefinition Definition = new("ss-capture", "SSCapture", "SSCapture.exe", "README.md",
        AppId: "KotorinToys.SSCapture", InstalledExecutable: "SSCapture.exe");
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
            registration.Name != "SSCapture" && registration.Name != "SSCapture " + registration.Version || !PayloadManifest.VersionNumber(registration.Version))
            throw new IOException("正式登録・製品名・導入先が一致しません。");
        var uninstaller = SafeFiles.Resolve(root, "unins000.exe");
        if (!string.Equals(registration.Uninstall.Trim().Trim('"'), uninstaller, StringComparison.OrdinalIgnoreCase) || !File.Exists(uninstaller))
            throw new IOException("削除登録が対象製品の導入先と一致しません。");
        var identity = (readExecutable ?? ReadExecutable)(root);
        if (identity.Product != "SSCapture" || identity.Version != registration.Version ||
            identity.FileVersion != registration.Version + ".0" || identity.AssemblyVersion != registration.Version + ".0")
            throw new IOException("実EXEの製品名・版が正式登録と一致しません。");
        var definitionPath = SafeFiles.Resolve(root, "tool.json");
        if (!File.Exists(definitionPath))
        {
            if (!allowLegacy || registration.Version != "0.1.2") throw new IOException("製品定義がありません。既知旧版以外は修復しません。");
            var old = legacy ?? PayloadManifest.Legacy();
            old.Verify(root);
            return new(root, registration.Version, true, false, old);
        }
        using var document = SafeFiles.Read<JsonDocument>(definitionPath);
        var json = document.RootElement;
        if (json.GetProperty("schemaVersion").GetInt32() != 1 || json.GetProperty("id").GetString() != "ss-capture" ||
            json.GetProperty("name").GetString() != "SSCapture" || json.GetProperty("appId").GetString() != "KotorinToys.SSCapture" ||
            json.GetProperty("version").GetString() != registration.Version || json.GetProperty("installedExecutable").GetString() != "SSCapture.exe")
            throw new IOException("製品定義の識別が一致しません。");
        var manifest = PayloadManifest.Read(SafeFiles.Resolve(root, "payload-manifest.json"));
        if (manifest.Version != registration.Version) throw new IOException("導入済み目録の版が一致しません。");
        manifest.Verify(root);
        return new(root, registration.Version, false, false, manifest);
    }
}
