using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace SSCapture.Maintenance;

internal static class SafeFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Root(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("ローカルの絶対パスが必要です。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root.Length < 4 || new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments }
            .Any(folder => string.Equals(root, Environment.GetFolderPath(folder), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("広いシステム・利用者フォルダーを製品の対象にできません。");
        CheckLinks(root);
        return root;
    }
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("相対パスが不正です。");
        var value = Path.GetFullPath(Path.Combine(root, relative));
        if (!value.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("製品の範囲外を参照しています。");
        CheckLinks(value);
        return value;
    }
    public static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンク経由の配置は処理しません。");
    }
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static T Read<T>(string path)
    {
        CheckLinks(path);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("管理記録の容量上限を超えています。");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) ?? throw new IOException("管理記録が空です。");
    }
    public static void Write<T>(string path, T data)
    {
        CheckLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, data, Json); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    public static void PrivateDirectory(string path)
    {
        CheckLinks(path);
        var directory = Directory.CreateDirectory(path);
        var access = new DirectorySecurity();
        access.SetAccessRuleProtection(true, false);
        var sid = WindowsIdentity.GetCurrent().User!;
        access.SetOwner(sid);
        access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(access);
    }
    public static void RestoreFile(string source, string destination)
    {
        CheckLinks(source); CheckLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
        File.Copy(source, temporary, false);
        File.Move(temporary, destination, true);
    }
}

internal sealed record PayloadFile(string Path, long Length, string Sha256);
internal sealed record PayloadManifest(int SchemaVersion, string Id, string Version, PayloadFile[] Files, string? SetupSha256 = null)
{
    public static PayloadManifest Read(string path)
    {
        var value = SafeFiles.Read<PayloadManifest>(path);
        if (value.SchemaVersion != 1 || value.Id != "ss-capture" || !VersionNumber(value.Version) || value.Files is not { Length: > 0 and < 5000 })
            throw new IOException("配布目録の識別が不正です。");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in value.Files)
            if (!unique.Add(file.Path.Replace('\\', '/')) || file.Length < 0 || !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$") || !OwnedPath(file.Path))
                throw new IOException("配布目録の項目が不正です。");
        return value;
    }
    public static bool VersionNumber(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d+\.\d+$");
    public static bool OwnedPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
            p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ||
            Path.IsPathRooted(path)) return false;
        if (parts.Any(p => new[] { "data", "tools", "Captures", ".git", "private", "temp" }.Contains(p, StringComparer.OrdinalIgnoreCase))) return false;
        return new[] { ".exe", ".dll", ".json", ".md", ".txt" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
    public void Verify(string root)
    {
        foreach (var file in Files)
        {
            var path = SafeFiles.Resolve(root, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length || SafeFiles.Hash(path) != file.Sha256)
                throw new IOException("配布ファイルの照合に失敗しました: " + file.Path);
        }
    }
    public static PayloadManifest Legacy(string version)
    {
        if (version is not ("0.1.1" or "0.1.2")) throw new IOException("この旧版の配布物は照合できません。");
        var assembly = typeof(PayloadManifest).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("legacy-" + version + ".json", StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<PayloadManifest>(stream, SafeFiles.Json)!;
    }
}
