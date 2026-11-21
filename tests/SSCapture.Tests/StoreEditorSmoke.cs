using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int OpenStoreEditorSmoke(string[] args)
    {
        if (args.Length != 3 || args[1] is not ("photos" or "clipchamp"))
        {
            Console.Error.WriteLine("使い方: --store-editor-open photos|clipchamp <検証用ファイル>");
            return 2;
        }

        try
        {
            string path = Path.GetFullPath(args[2]);
            if (!File.Exists(path)) throw new FileNotFoundException("検証用ファイルが見つかりません。", path);
            string appId = args[1] == "photos"
                ? "Microsoft.Windows.Photos_8wekyb3d8bbwe!App"
                : "Clipchamp.Clipchamp_yxz26nhyzhsrt!App";
            OpenStoreFile(appId, path);
            Console.WriteLine($"HANDED OFF {args[1]}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ファイルを渡せません: {ex.Message} (0x{ex.HResult:X8})");
            return 1;
        }
    }

    private static void OpenStoreFile(string appId, string path)
    {
        string extension = Path.GetExtension(path);
        using var registered = Registry.ClassesRoot.OpenSubKey($@"{extension}\OpenWithProgids");
        using var user = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\OpenWithProgids");
        var candidates = (registered?.GetValueNames() ?? []).Concat(user?.GetValueNames() ?? []);
        string? progId = candidates.FirstOrDefault(candidate =>
        {
            using var application = Registry.ClassesRoot.OpenSubKey($@"{candidate}\Application");
            return string.Equals(application?.GetValue("AppUserModelID") as string, appId, StringComparison.OrdinalIgnoreCase);
        });
        if (progId is null) throw new IOException($"対象アプリは {extension} の「プログラムから開く」に登録されていません。");

        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = 0x0001 /* SEE_MASK_CLASSNAME */ | 0x0100 /* SEE_MASK_NOASYNC */ | 0x0400 /* SEE_MASK_FLAG_NO_UI */,
            Verb = "open",
            File = path,
            Show = 1,
            Class = progId
        };
        if (!ShellExecuteExW(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

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
