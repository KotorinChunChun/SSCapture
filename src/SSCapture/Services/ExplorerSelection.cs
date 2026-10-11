using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal static class ExplorerSelection
{
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    // Retained for callers that inject a process launcher. Production Open uses
    // the Shell selection API directly and does not start an Explorer process.
    internal static ProcessStartInfo StartInfo(string path)
    {
        string fullPath = ValidateFile(path);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Path.IsPathFullyQualified(windows))
            throw new InvalidOperationException("Windowsフォルダを取得できません。");
        // Explorer parses /select itself. Quote the entire path so spaces and
        // commas stay inside the selected item; never route it through a shell.
        return new ProcessStartInfo(Path.Combine(windows, "explorer.exe"))
        {
            Arguments = "/select,\"" + fullPath + "\"",
            UseShellExecute = false
        };
    }

    internal static void Open(string path, Action<string>? select = null)
    {
        string fullPath = ValidateFile(path);
        (select ?? NativeOpen)(fullPath);
    }

    private static string ValidateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("選択するファイルは絶対パスで指定してください。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("選択する保存ファイルがありません。", fullPath);
        return fullPath;
    }

    private static void NativeOpen(string path)
    {
        int initialized = CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        if (initialized < 0 && initialized != RpcEChangedMode) Marshal.ThrowExceptionForHR(initialized);
        nint item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out item, 0, out _));
            if (item == 0) throw new IOException("保存ファイルの選択情報を取得できません。");
            // With cidl=0, this absolute item PIDL opens its parent and selects
            // exactly this item. No retry, second window, or UI key injection.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0));
        }
        finally
        {
            if (item != 0) Marshal.FreeCoTaskMem(item);
            // S_OK and S_FALSE each add one initialization count. A changed
            // apartment mode is already initialized but adds no count to release.
            if (initialized >= 0) CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);
}
