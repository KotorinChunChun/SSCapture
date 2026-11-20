namespace SSCapture.Services;

internal static class EditorExecutable
{
    internal static string LegacyPaint => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe");
    internal static string PaintAlias => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "mspaint.exe");

    internal static string? FindPPPaint()
    {
        using var registered = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.PPPaint_is1");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sibling = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        return FindPPPaint([registered?.GetValue("InstallLocation") as string,
            sibling is null ? null : Path.Combine(sibling, "PPPaint"),
            Path.Combine(local, "Programs", "PPPaint")], File.Exists);
    }

    internal static string? FindPPPaint(IEnumerable<string?> directories, Func<string, bool> exists) =>
        directories.Where(directory => !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory))
            .Select(directory => Path.Combine(directory!, "PPPaint.exe")).FirstOrDefault(exists);

    internal static string PaintPreset
    {
        get
        {
            if (File.Exists(LegacyPaint)) return LegacyPaint;
            return File.Exists(PaintAlias) ? PaintAlias : LegacyPaint;
        }
    }

    internal static string Resolve(string executable, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable)
            || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("設定画面で外部アプリのexeファイルを指定してください。");
        exists ??= File.Exists;
        string full = Path.GetFullPath(executable);
        if (exists(full)) return full;
        if (IsLegacyPaint(full) && exists(PaintAlias)) return PaintAlias;
        throw new FileNotFoundException("外部アプリの実行ファイルが見つかりません: " + full, full);
    }

    private static bool IsLegacyPaint(string executable)
    {
        string legacy32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "mspaint.exe");
        return string.Equals(executable, LegacyPaint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(executable, legacy32, StringComparison.OrdinalIgnoreCase);
    }
}
