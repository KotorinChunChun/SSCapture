using Microsoft.Win32;
using System.Text.Json;

namespace SSCapture.Maintenance;

internal sealed record RegistryValueBackup(string Name, RegistryValueKind Kind, string Json);
internal sealed record RegistrationBackup(bool Exists, RegistryValueBackup[] Values)
{
    public static RegistrationBackup Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(TargetValidator.RegistryPath);
        if (key is null) return new(false, []);
        if (key.SubKeyCount != 0) throw new IOException("削除登録に未知の下位キーがあります。");
        return new(true, key.GetValueNames().Select(name => new RegistryValueBackup(name, key.GetValueKind(name),
            JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))).ToArray());
    }
    public void ValidateCurrent(string root, string oldVersion, string newVersion)
    {
        var current = TargetValidator.ReadRegistration();
        if (current != null && (!string.Equals(SafeFiles.Root(current.Directory), root, StringComparison.OrdinalIgnoreCase) ||
            current.Version != oldVersion && current.Version != newVersion ||
            !TargetValidator.IsProductRegistrationName(current.Name, current.Version)))
            throw new IOException("削除登録が外部変更されているため復元しません。");
        using var key = Registry.CurrentUser.OpenSubKey(TargetValidator.RegistryPath);
        if (key is { SubKeyCount: not 0 }) throw new IOException("削除登録の下位キーを変更しません。");
    }
    public void Restore(string root, string oldVersion, string newVersion)
    {
        ValidateCurrent(root, oldVersion, newVersion);
        if (!Exists)
        {
            Registry.CurrentUser.DeleteSubKey(TargetValidator.RegistryPath, false);
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(TargetValidator.RegistryPath);
        if (key.SubKeyCount != 0) throw new IOException("削除登録の下位キーを変更しません。");
        foreach (var name in key.GetValueNames())
            if (!Values.Any(v => v.Name == name)) key.DeleteValue(name);
        foreach (var value in Values)
        {
            object data = value.Kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Deserialize<string>(value.Json)!,
                RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(value.Json),
                RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(value.Json),
                RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(value.Json)!,
                RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value.Json)!,
                _ => throw new IOException("未対応の登録値です。")
            };
            key.SetValue(value.Name, data, value.Kind);
        }
    }
}
