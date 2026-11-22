using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Win32;

namespace SSCapture.Services;

internal sealed class ExternalEditor(Action<ProcessStartInfo>? start = null,
    Func<ExternalApplicationLaunch, string?>? findAssociation = null, Action<string, string>? openAssociated = null)
{
    internal bool CanOpen(ExternalApplication app)
    {
        try { _ = Resolve(app); return true; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    internal Action<string> Resolve(ExternalApplication? app)
    {
        if (app is null) throw new ArgumentException("設定画面で外部アプリを指定してください。");
        Action<string> open;
        if (app.Launch == ExternalApplicationLaunch.Executable)
        {
            string executable = EditorExecutable.Resolve(app.Executable);
            EditorArguments.Validate(app.Arguments);
            open = path => (start ?? Start)(OutputRouter.EditorStartInfo(executable, path, app.Arguments));
        }
        else
        {
            string association = (findAssociation ?? PngEditorAssociation.Find)(app.Launch)
                ?? throw new IOException($"{app.Name} のPNG関連付けが見つかりません。インストール状態を確認してください。");
            open = path =>
            {
                if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"{app.Name} に渡せる形式はPNGです。");
                (openAssociated ?? PngEditorAssociation.Open)(association, path);
            };
        }

        return path =>
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("送信する保存済みファイルがありません。", path);
            try { open(path); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
            {
                throw new IOException($"{app.Name} を起動できません。ファイルは保持されています: {path}", ex);
            }
        };
    }

    private static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info) ?? throw new IOException("外部アプリのプロセスを開始できません。");
    }
}

internal static class PngEditorAssociation
{
    internal const string SnippingToolAppId = "Microsoft.ScreenSketch_8wekyb3d8bbwe!App";
    private const string PerUserPng = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.png";

    internal static string? Find(ExternalApplicationLaunch kind)
    {
        if (kind != ExternalApplicationLaunch.SnippingToolPng) return null;
        try
        {
            using var classes = Registry.ClassesRoot;
            using var png = classes.OpenSubKey(".png");
            using var user = Registry.CurrentUser.OpenSubKey(PerUserPng);
            var candidates = ReadCandidates(png).Concat(ReadCandidates(user))
                .Concat(ReadCapabilities(Registry.CurrentUser)).Concat(ReadCapabilities(Registry.LocalMachine));
            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var association = classes.OpenSubKey(candidate);
                using var open = association?.OpenSubKey(@"shell\open\command");
                if (open is null) continue;
                using var application = association?.OpenSubKey("Application");
                string? appId = application?.GetValue("AppUserModelID") as string;
                if (Matches(appId)) return candidate;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    private static IEnumerable<string> ReadCandidates(RegistryKey? extension)
    {
        using var progIds = extension?.OpenSubKey("OpenWithProgids");
        foreach (string id in progIds?.GetValueNames() ?? [])
            if (id.Length != 0) yield return id;

        string? defaultClass = extension?.GetValue(null) as string;
        if (!string.IsNullOrWhiteSpace(defaultClass)) yield return defaultClass;
        using var choice = extension?.OpenSubKey("UserChoice");
        if (choice?.GetValue("ProgId") is string chosen && chosen.Length != 0) yield return chosen;
    }

    private static IEnumerable<string> ReadCapabilities(RegistryKey hive)
    {
        using var apps = hive.OpenSubKey(@"Software\RegisteredApplications");
        foreach (string name in apps?.GetValueNames() ?? [])
        {
            if (apps?.GetValue(name) is not string capabilities) continue;
            using var associations = hive.OpenSubKey(capabilities + @"\FileAssociations");
            if (associations?.GetValue(".png") is string id && id.Length != 0) yield return id;
        }
    }

    internal static bool Matches(string? appId) => string.Equals(appId, SnippingToolAppId, StringComparison.OrdinalIgnoreCase);

    internal static void Open(string association, string path)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            OpenOnSta(association, path);
            return;
        }
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { OpenOnSta(association, path); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    private static void OpenOnSta(string association, string path)
    {
        // Shell の拡張を呼び出せるよう、起動スレッドで COM の STA を初期化する。
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2 | 4));
        try
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = 0x0001 /* SEE_MASK_CLASSNAME */ | 0x0100 /* SEE_MASK_NOASYNC */ | 0x0400 /* SEE_MASK_FLAG_NO_UI */,
                Verb = "open", File = path, Show = 1, Class = association
            };
            if (!ShellExecuteExW(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CoUninitialize(); }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal nint Window;
        internal string? Verb, File, Parameters, Directory;
        internal int Show;
        internal nint Instance, IdList;
        internal string? Class;
        internal nint ClassKey;
        internal uint HotKey;
        internal nint IconOrMonitor, Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
}
