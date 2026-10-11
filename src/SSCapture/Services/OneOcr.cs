using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SSCapture.Services;

internal sealed record OcrResult(string[] Lines, string? Error = null);

internal static class OneOcr
{
    internal static string FindEngine(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return ValidateEngine(configured);
        var candidates = new List<string>();
        // Bundle registration is readable even when WindowsApps cannot be enumerated.
        using var applications = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications");
        if (applications is not null)
        {
            foreach (string key in applications.GetSubKeyNames().Where(name => name.StartsWith("Microsoft.ScreenSketch_", StringComparison.Ordinal)))
            {
                try
                {
                    using var entry = applications.OpenSubKey(key);
                    if (entry?.GetValue("Path") is not string manifest || !File.Exists(manifest)) continue;
                    string? bundle = Path.GetDirectoryName(Path.GetDirectoryName(manifest));
                    string? root = Path.GetDirectoryName(bundle);
                    if (root is null) continue;
                    var document = XDocument.Load(manifest);
                    foreach (var package in document.Descendants().Where(element => element.Name.LocalName == "Package" &&
                        (string?)element.Attribute("Type") == "application" && (string?)element.Attribute("Architecture") == "x64"))
                    {
                        if (!Version.TryParse((string?)package.Attribute("Version"), out var version)) continue;
                        candidates.Add(Path.Combine(root, $"Microsoft.ScreenSketch_{version}_x64__{key.Split('_')[^1]}", "SnippingTool"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or System.Security.SecurityException) { }
            }
        }
        foreach (string folder in candidates.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            if (RequiredFiles.All(name => File.Exists(Path.Combine(folder, name)))) return folder;
        throw new FileNotFoundException("OneOCRを自動検出できません。設定のOneOCRフォルダにエンジン・モデルのある場所を指定してください。");
    }

    private static readonly string[] RequiredFiles = ["oneocr.dll", "oneocr.onemodel", "onnxruntime.dll"];
    private static string ValidateEngine(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !RequiredFiles.All(name => File.Exists(Path.Combine(folder, name))))
            throw new FileNotFoundException("OneOCRフォルダにはoneocr.dll、oneocr.onemodel、onnxruntime.dllが必要です。");
        return Path.GetFullPath(folder);
    }

    private static string PrepareEngine(string source)
    {
        // Packaged WindowsApps DLLs may be readable but not loadable by unpackaged
        // programs. Cache only this machine's installed components, never ship them.
        string identity = source + string.Join("|", RequiredFiles.Select(name =>
        {
            var file = new FileInfo(Path.Combine(source, name));
            return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        string cache = Path.Combine(AppContext.BaseDirectory, "data", "oneocr", fingerprint);
        Directory.CreateDirectory(cache);
        foreach (string name in RequiredFiles)
        {
            string original = Path.Combine(source, name), target = Path.Combine(cache, name);
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(original).Length) continue;
            string temporary = Path.Combine(cache, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try { File.Copy(original, temporary, false); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return cache;
    }

    internal static async Task<OcrResult> RecognizeAsync(Bitmap image, string configured, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "SSCapture-OCR-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(folder, "input.png"), output = Path.Combine(folder, "result.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process();
        try
        {
            string engine = PrepareEngine(FindEngine(configured));
            Directory.CreateDirectory(folder);
            image.Save(input, ImageFormat.Png);
            string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
            var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
            foreach (string argument in new[] { typeof(OneOcr).Assembly.Location, "--ocr-worker", input, output, engine }) start.ArgumentList.Add(argument);
            process.StartInfo = start;
            if (!process.Start()) throw new IOException("OneOCRを開始できません。");
            // Native DLL diagnostics must never block the worker's pipes.
            Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (!File.Exists(output)) throw new IOException($"OneOCRが結果を返さず終了しました（{process.ExitCode}）。");
            OcrResult result = JsonSerializer.Deserialize<OcrResult>(await File.ReadAllTextAsync(output, timeout.Token).ConfigureAwait(false))
                ?? throw new IOException("OneOCRの結果が空です。");
            if (process.ExitCode != 0 && result.Error is null) throw new IOException("OneOCRの実行に失敗しました。");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new OcrResult([], "OneOCRが45秒以内に応答しませんでした。手入力できます。"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new OcrResult([], ex.Message); }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            foreach (string path in new[] { input, output })
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(folder)) Directory.Delete(folder, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static int RunWorker(string[] args)
    {
        if (args.Length != 4) return 2;
        OcrResult result;
        try
        {
            using var image = new Bitmap(args[1]);
            result = new OcrResult(OneOcrNative.Read(image, ValidateEngine(args[3])));
        }
        catch (Exception ex) { result = new OcrResult([], ex.Message); }
        File.WriteAllText(args[2], JsonSerializer.Serialize(result));
        return result.Error is null ? 0 : 1;
    }
}

internal static class OneOcrNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage { public int Type, Width, Height, Reserved; public long Stride; public nint Data; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Create(out long handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Delay(long handle, byte value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Pipeline([MarshalAs(UnmanagedType.LPUTF8Str)] string model, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, long options, out long handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Run(long pipeline, ref NativeImage image, long options, out long result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Count(long result, out long count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Line(long result, long index, out long line);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Content(long line, out nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(long handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);

    internal static string[] Read(Bitmap source, string directory)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("OneOCRはx64プロセスで実行してください。");
        nint library = LoadLibraryEx(Path.Combine(directory, "oneocr.dll"), 0, 0x1100);
        if (library == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"OneOCR DLLを読み込めません（{error}: {new Win32Exception(error).Message}）。");
        }
        long init = 0, pipeline = 0, options = 0, result = 0;
        T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        void Check(long status) { if (status != 0) throw new IOException($"OneOCRエラー: {status}"); }
        try
        {
            Check(Bind<Create>("CreateOcrInitOptions")(out init));
            Check(Bind<Delay>("OcrInitOptionsSetUseModelDelayLoad")(init, 0));
            Check(Bind<Pipeline>("CreateOcrPipeline")(Path.Combine(directory, "oneocr.onemodel"), "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4", init, out pipeline));
            Check(Bind<Create>("CreateOcrProcessOptions")(out options));
            double scale = Math.Min(1d, 10000d / Math.Max(source.Width, source.Height));
            int width = Math.Max(1, (int)(source.Width * scale)), height = Math.Max(1, (int)(source.Height * scale));
            using var pixels = new Bitmap(Math.Max(50, width), Math.Max(50, height), PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(pixels)) { graphics.Clear(Color.White); graphics.DrawImage(source, new Rectangle(0, 0, width, height)); }
            BitmapData data = pixels.LockBits(new Rectangle(Point.Empty, pixels.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var image = new NativeImage { Type = 3, Width = pixels.Width, Height = pixels.Height, Stride = data.Stride, Data = data.Scan0 };
                Check(Bind<Run>("RunOcrPipeline")(pipeline, ref image, options, out result));
                Check(Bind<Count>("GetOcrLineCount")(result, out long count));
                if (count is < 0 or > 10000) throw new IOException("OneOCRの認識行数が不正です。");
                var lines = new List<string>();
                for (long index = 0; index < count; index++)
                {
                    Check(Bind<Line>("GetOcrLine")(result, index, out long line));
                    Check(Bind<Content>("GetOcrLineContent")(line, out nint text));
                    string value = Marshal.PtrToStringUTF8(text) ?? "";
                    if (!string.IsNullOrWhiteSpace(value)) lines.Add(value);
                }
                return lines.ToArray();
            }
            finally { pixels.UnlockBits(data); }
        }
        finally
        {
            try
            {
                foreach (var entry in new[] { (result, "ReleaseOcrResult"), (options, "ReleaseOcrProcessOptions"), (pipeline, "ReleaseOcrPipeline"), (init, "ReleaseOcrInitOptions") })
                    if (entry.Item1 != 0) Bind<Release>(entry.Item2)(entry.Item1);
            }
            finally { NativeLibrary.Free(library); }
        }
    }
}
