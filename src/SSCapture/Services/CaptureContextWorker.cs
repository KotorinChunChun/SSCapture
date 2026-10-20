using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SSCapture.Services;

// UIAプロバイダーは停止することがあるため、主プロセスのSTAやmutexより前の専用MTAでのみ呼ぶ。
internal static class CaptureContextWorker
{
    internal static CaptureContext Unavailable(string reason) => new(DateTimeOffset.Now,
        new("unavailable", reason), new("unavailable", reason), new("unavailable", reason), new("unavailable", reason));

    internal static int Run()
    {
        int result = 1;
        var thread = new Thread(() =>
        {
            try
            {
                Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
                object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"), true)!)!;
                try
                {
                    var automation = (IAutomation)instance;
                    Console.WriteLine("ready");
                    string? line;
                    while ((line = Console.ReadLine()) is not null && line.Length < 1024)
                    {
                        PixelPoint point = JsonSerializer.Deserialize<PixelPoint>(line)!;
                        DateTimeOffset observed = DateTimeOffset.Now;
                        ElementMetadata focused = ReadElement(() => automation.Focused());
                        ElementMetadata under = ReadElement(() => automation.FromPoint(new NativePoint(point.X, point.Y)));
                        var context = new CaptureContext(observed, focused, under, ReadApplication(GetForegroundWindow()),
                            ReadApplication(GetAncestor(WindowFromPoint(new NativePoint(point.X, point.Y)), 2)));
                        Console.WriteLine(JsonSerializer.Serialize(context, new JsonSerializerOptions(CaptureMetadata.JsonOptions) { WriteIndented = false }));
                    }
                    result = 0;
                }
                finally { Marshal.FinalReleaseComObject(instance); }
            }
            catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException) { Console.Error.WriteLine(e.GetType().Name); }
        });
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join(); return result;
    }

    private static ElementMetadata ReadElement(Func<IElement> acquire)
    {
        IElement? element = null;
        try
        {
            element = acquire(); if (element is null) return new("unavailable", "elementNotAvailable");
            object? Value(int id) => element.Property(id);
            string? Text(int id) => Value(id) as string is { } text ? text[..Math.Min(text.Length, 2048)] : null;
            bool password = Value(30019) is not false; // 不明な場合もNameを読まない。
            PixelBounds? bounds = Value(30001) is double[] { Length: 4 } r && r.All(double.IsFinite)
                ? new((int)r[0], (int)r[1], (int)r[2], (int)r[3]) : null;
            return new("ok", AutomationId: Text(30011), ControlType: Value(30003) as int?, Name: password ? null : Text(30005),
                ClassName: Text(30012), FrameworkId: Text(30024), Bounds: bounds, ProcessId: Value(30002) as int?);
        }
        catch (COMException e) { return new(e.HResult == unchecked((int)0x80070005) ? "accessDenied" : "unavailable", $"HRESULT:0x{e.HResult:X8}"); }
        finally { if (element is not null) Marshal.FinalReleaseComObject(element); }
    }

    internal static ApplicationMetadata ReadApplication(nint window)
    {
        if (window == 0) return new("unavailable", "windowNotAvailable");
        GetWindowThreadProcessId(window, out uint pid);
        var title = new StringBuilder(2048); var cls = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity); GetClassName(window, cls, cls.Capacity);
        PixelBounds? bounds = GetWindowRect(window, out NativeRect r) ? new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;
        string? path = null, reason = null;
        try { using var p = Process.GetProcessById((int)pid); path = p.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { reason = e is System.ComponentModel.Win32Exception ? "accessDenied" : "processExited"; }
        return new(reason is null ? "ok" : "partial", reason, (int)pid, path is null ? null : System.IO.Path.GetFileName(path), path, cls.ToString(), title.ToString(), bounds);
    }

    // Windows SDK 10.0.26100 UIAutomationClient.h の先頭vtable順。未使用メソッドもスロットを保持。
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements(nint a, nint b, out int same);
        void CompareRuntimeIds(nint a, nint b, out int same);
        IElement Root(); IElement FromHandle(nint handle); IElement FromPoint(NativePoint point); IElement Focused();
    }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void SetFocus(); nint RuntimeId(); nint FindFirst(int scope, nint condition); nint FindAll(int scope, nint condition);
        nint FindFirstBuildCache(int scope, nint condition, nint cache); nint FindAllBuildCache(int scope, nint condition, nint cache);
        nint BuildUpdatedCache(nint cache);
        [return: MarshalAs(UnmanagedType.Struct)] object Property(int id);
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
}

internal sealed class CaptureContextClient(Func<ProcessStartInfo>? startFactory = null) : IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim gate = new(1);
    internal static CaptureContextClient Shared { get; } = new();
    internal bool IsStarted => process is not null;
    internal async Task<CaptureContext> ObserveAsync(Point point, bool enabled = true, int timeoutMilliseconds = 250)
    {
        if (!enabled) return CaptureContextWorker.Unavailable("disabled");
        if (!await gate.WaitAsync(0)) return CaptureContextWorker.Unavailable("workerBusy");
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            if (process is null || process.HasExited)
            {
                DisposeProcess();
                string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
                start.ArgumentList.Add(typeof(CaptureContextWorker).Assembly.Location); start.ArgumentList.Add("--capture-context-worker");
                process = Process.Start(startFactory?.Invoke() ?? start) ?? throw new IOException("UIA workerを開始できません。");
                _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                if (await ReadBoundedAsync(process.StandardOutput, 64, timeout.Token) != "ready") throw new IOException("UIA workerが応答しません。");
            }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(PixelPoint.From(point)).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            string line = await ReadBoundedAsync(process.StandardOutput, 65536, timeout.Token);
            return JsonSerializer.Deserialize<CaptureContext>(line, CaptureMetadata.JsonOptions) ?? CaptureContextWorker.Unavailable("emptyResponse");
        }
        catch (OperationCanceledException) { DisposeProcess(); return CaptureContextWorker.Unavailable("timeout"); }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        { DisposeProcess(); return CaptureContextWorker.Unavailable(e.GetType().Name); }
        finally { gate.Release(); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (result.Length <= limit)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), token) == 0) throw new IOException("UIA workerが終了しました。");
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(buffer[0]);
        }
        throw new IOException("UIA workerの応答が上限を超えました。");
    }
    private void DisposeProcess()
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose(); process = null;
    }
    public void Dispose() => DisposeProcess();
}
