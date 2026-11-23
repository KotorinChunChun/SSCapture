using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SSCapture.Capture;

namespace SSCapture.Services;

internal sealed record WindowCaptureCommand(nint? Handle, string? ProcessName, string? Title, bool ClientAreaOnly)
{
    internal void Validate()
    {
        if (Handle.HasValue && (Handle == 0 || ProcessName is not null || Title is not null)
            || !Handle.HasValue && string.IsNullOrWhiteSpace(ProcessName))
            throw new ArgumentException("--capture-window には --hwnd または --process（必要に応じて --title）を指定してください。");
        if (ProcessName is not null && (ProcessName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || ProcessName.Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("--process には実行ファイルの名前を指定してください。");
    }

    internal static nint ParseHandle(string value)
    {
        bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!nuint.TryParse(hex ? value.AsSpan(2) : value.AsSpan(), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out nuint handle) || handle == 0)
            throw new ArgumentException("--hwnd には有効な十進数または16進数のウィンドウハンドルを指定してください。");
        return unchecked((nint)handle);
    }

    internal CaptureWindow Resolve() => Resolve(CaptureNative.EnumerateCaptureWindows(), ReadProcessName);

    internal CaptureWindow Resolve(IEnumerable<CaptureWindow> windows, Func<uint, string?> readProcessName)
    {
        if (Handle is nint handle)
            return windows.FirstOrDefault(window => window.Handle == handle)
                ?? throw new InvalidOperationException("指定されたハンドルの撮影可能なウィンドウが見つかりません。");

        string? requestedName = ProcessName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? ProcessName[..^4] : ProcessName;
        var candidates = windows.Where(window => Title is null || StringComparer.Ordinal.Equals(Title, window.Title)).ToArray();
        var matchingProcesses = candidates.Select(window => window.ProcessId).Distinct()
            .Where(pid => StringComparer.OrdinalIgnoreCase.Equals(requestedName, readProcessName(pid))).ToHashSet();
        var matches = candidates.Where(window => matchingProcesses.Contains(window.ProcessId)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("プロセス名とタイトルに一致する撮影可能なウィンドウが見つかりません。"),
            _ => throw new InvalidOperationException("一致するウィンドウが複数あります。タイトルまたは --hwnd で1つに絞ってください。")
        };
    }

    private static string? ReadProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            return null;
        }
    }
}
