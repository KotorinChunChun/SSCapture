using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SSCapture.Services;

internal sealed record OcrResult(string[] Lines, string? Error = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OcrLineLayout?[]? Layout { get; init; }
}

internal static class OneOcr
{
    private const string LayoutMode = "--layout";
    private const string TextMode = "--text";

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

    internal static Task<OcrResult> RecognizeAsync(Bitmap image, string configured, CancellationToken cancellationToken) =>
        RecognizeCoreAsync(image, configured, null, cancellationToken);

    internal static Task<OcrResult> RecognizeForCopyAsync(Bitmap image, string configured, CancellationToken cancellationToken) =>
        RecognizeCoreAsync(image, configured, LayoutMode, cancellationToken);

    private static async Task<OcrResult> RecognizeCoreAsync(Bitmap image, string configured, string? mode, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "SSCapture-OCR-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(folder, "input.png"), output = Path.Combine(folder, "result.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            string engine = PrepareEngine(FindEngine(configured));
            Directory.CreateDirectory(folder);
            image.Save(input, ImageFormat.Png);
            OcrResult? result = await ReadWorkerAsync(folder, input, output, engine, mode, timeout.Token).ConfigureAwait(false);
            // A changed native layout ABI can terminate the worker. Retry with the same budget and text-only exports.
            return result ?? await ReadWorkerAsync(folder, input, output, engine, TextMode, timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("OneOCRが結果を返さず終了しました。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new OcrResult([], "OneOCRが45秒以内に応答しませんでした。手入力できます。"); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new OcrResult([], ex.Message); }
        finally
        {
            foreach (string path in new[] { input, output })
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(folder)) Directory.Delete(folder, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<OcrResult?> ReadWorkerAsync(string folder, string input, string output, string engine,
        string? mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string assembly = typeof(OneOcr).Assembly.Location;
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        bool useAppHost = File.Exists(Path.Combine(AppContext.BaseDirectory, "hostpolicy.dll")) || !File.Exists(dotnet);
        var start = new ProcessStartInfo(useAppHost ? Path.ChangeExtension(assembly, ".exe") : dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = folder
        };
        if (!useAppHost) start.ArgumentList.Add(assembly);
        foreach (string argument in new[] { "--ocr-worker", input, output, engine }) start.ArgumentList.Add(argument);
        if (mode is not null) start.ArgumentList.Add(mode);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("OneOCRを開始できません。");
        try
        {
            // Native DLL diagnostics must never block the worker's pipes.
            Task stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (!File.Exists(output))
            {
                if (mode == LayoutMode) return null;
                throw new IOException($"OneOCRが結果を返さず終了しました（{process.ExitCode}）。");
            }
            var result = JsonSerializer.Deserialize<OcrResult>(await File.ReadAllTextAsync(output, token).ConfigureAwait(false))
                ?? throw new IOException("OneOCRの結果が空です。");
            if (process.ExitCode != 0 && result.Error is null) throw new IOException("OneOCRの実行に失敗しました。");
            return result;
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
    }

    internal static int RunWorker(string[] args)
    {
        if (args.Length != 4 && (args.Length != 5 || args[4] is not (LayoutMode or TextMode))) return 2;
        OcrResult result;
        try
        {
            using var image = new Bitmap(args[1]);
            result = OneOcrNative.Read(image, ValidateEngine(args[3]), args.Length == 5, args.Length == 5 && args[4] == LayoutMode);
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MaxLineCount(long options, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BoundingBox(long handle, out nint box);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(long handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);

    internal static OcrResult Read(Bitmap source, string directory, bool forCopy, bool includeLayout)
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
        T? BindOptional<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(library, name, out nint address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
        void Check(long status) { if (status != 0) throw new IOException($"OneOCRエラー: {status}"); }
        try
        {
            Check(Bind<Create>("CreateOcrInitOptions")(out init));
            Check(Bind<Delay>("OcrInitOptionsSetUseModelDelayLoad")(init, 0));
            Check(Bind<Pipeline>("CreateOcrPipeline")(Path.Combine(directory, "oneocr.onemodel"), "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4", init, out pipeline));
            Check(Bind<Create>("CreateOcrProcessOptions")(out options));
            if (forCopy) BindOptional<MaxLineCount>("OcrProcessOptionsSetMaxRecognitionLineCount")?.Invoke(options, 1000);
            double scale = Math.Min(1d, 10000d / Math.Max(source.Width, source.Height));
            var original = RecognizeAtScale(source, scale);
            if (!includeLayout) return original;
            if (original.Lines.Length == 0 && (long)source.Width * source.Height <= 450_000 && Math.Max(source.Width, source.Height) <= 3000)
                return RecognizeAtScale(source, 2);
            if (original.Layout is not { } layout) return original;

            var elapsed = Stopwatch.StartNew();
            int attempts = 0;
            for (int i = 0; i < layout.Length && attempts < 8 && elapsed.Elapsed.TotalSeconds < 4; i++)
            {
                if (layout[i] is not { Bounds.Height: <= 16, Words.Length: > 0 } first || Confidence(first) is < 0 or >= 0.90f) continue;
                var box = first.Bounds;
                var region = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), Rectangle.FromLTRB(
                    (int)Math.Floor(box.Left) - 3, (int)Math.Floor(box.Top) - 3,
                    (int)Math.Ceiling(box.Right) + 3, (int)Math.Ceiling(box.Bottom) + 3));
                if (region.Width > 2000 || region.Width < region.Height * 2) continue;
                attempts++;
                using var crop = source.Clone(region, PixelFormat.Format32bppArgb);
                var second = RecognizeAtScale(crop, 3);
                if (second.Lines.Length != 1 || second.Lines[0] == original.Lines[i] || second.Layout?[0] is not { } reading
                    || Confidence(reading) < Math.Max(0.60f, Confidence(first) - 0.10f)) continue;
                var third = RecognizeAtScale(crop, 4);
                if (third.Lines.Length != 1 || third.Lines[0] != second.Lines[0] || third.Layout?[0] is not { } confirmed
                    || Confidence(confirmed) < Math.Max(0.60f, Confidence(first) - 0.10f)) continue;

                // Each recognized word is local to the crop. TSV expects original capture coordinates.
                OcrBounds Offset(OcrBounds position) => new(position.Left + region.X, position.Top + region.Y,
                    position.Right + region.X, position.Bottom + region.Y);
                var words = reading.Words!.Select(word => word with { Bounds = Offset(word.Bounds) }).ToArray();
                original.Lines[i] = second.Lines[0];
                layout[i] = new(Offset(reading.Bounds), words);
            }
            return original;

            OcrResult RecognizeAtScale(Bitmap input, double factor)
            {
                if (result != 0)
                {
                    Bind<Release>("ReleaseOcrResult")(result);
                    result = 0;
                }
                int width = Math.Max(1, (int)(input.Width * factor)), height = Math.Max(1, (int)(input.Height * factor));
                var scaled = new Size(width, height);
                using var pixels = new Bitmap(Math.Max(50, width), Math.Max(50, height), PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(pixels)) {
                    graphics.Clear(Color.White);
                    if (factor > 1)
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    }
                    graphics.DrawImage(input, new Rectangle(0, 0, width, height));
                }
                BitmapData data = pixels.LockBits(new Rectangle(Point.Empty, pixels.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var image = new NativeImage { Type = 3, Width = pixels.Width, Height = pixels.Height, Stride = data.Stride, Data = data.Scan0 };
                    Check(Bind<Run>("RunOcrPipeline")(pipeline, ref image, options, out result));
                    Check(Bind<Count>("GetOcrLineCount")(result, out long count));
                    if (count is < 0 or > 10000) throw new IOException("OneOCRの認識行数が不正です。");
                    var lines = new List<string>((int)count);
                    var lineBox = includeLayout ? BindOptional<BoundingBox>("GetOcrLineBoundingBox") : null;
                    var wordCount = includeLayout ? BindOptional<Count>("GetOcrLineWordCount") : null;
                    var getWord = includeLayout ? BindOptional<Line>("GetOcrWord") : null;
                    var wordContent = includeLayout ? BindOptional<Content>("GetOcrWordContent") : null;
                    var confidence = includeLayout ? BindOptional<WordConfidence>("GetOcrWordConfidence") : null;
                    var wordBox = includeLayout ? BindOptional<BoundingBox>("GetOcrWordBoundingBox") : null;
                    List<OcrLineLayout?>? layout = lineBox is null ? null : new((int)count);
                    int remainingWords = 100_000;
                    var getLine = Bind<Line>("GetOcrLine");
                    var lineContent = Bind<Content>("GetOcrLineContent");
                    for (long index = 0; index < count; index++)
                    {
                        Check(getLine(result, index, out long line));
                        Check(lineContent(line, out nint text));
                        string value = Marshal.PtrToStringUTF8(text) ?? "";
                        if (!forCopy && string.IsNullOrWhiteSpace(value)) continue;
                        lines.Add(value);
                        if (layout is null) continue;
                        var bounds = ReadBounds(line, lineBox!, pixels.Size, scaled, input.Size);
                        var words = bounds is null || wordCount is null || getWord is null || wordContent is null || wordBox is null
                            ? null : ReadWords(line, wordCount, getWord, wordContent, wordBox, confidence, pixels.Size,
                                scaled, input.Size, ref remainingWords);
                        layout.Add(bounds is { } box ? new OcrLineLayout(box, words) : null);
                    }
                    return new OcrResult(lines.ToArray()) { Layout = layout?.ToArray() };
                }
                finally { pixels.UnlockBits(data); }
            }
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long WordConfidence(long word, out float value);

    // The least certain word is useful for deciding whether one unclear character merits another reading.
    private static float Confidence(OcrLineLayout line) =>
        line.Words is { Length: > 0 } words ? words.Min(word => word.Confidence ?? -1f) : -1f;

    private static OcrWord[]? ReadWords(long line, Count count, Line get, Content content, BoundingBox box, WordConfidence? confidence,
        Size pixels, Size scaled, Size source, ref int remaining)
    {
        if (count(line, out long length) != 0 || length < 1 || length > remaining) return null;
        remaining -= (int)length;
        var words = new OcrWord[(int)length];
        for (int index = 0; index < words.Length; index++)
        {
            if (get(line, index, out long word) != 0 || word == 0 || content(word, out nint text) != 0) return null;
            string? value = Marshal.PtrToStringUTF8(text);
            var bounds = ReadBounds(word, box, pixels, scaled, source);
            if (string.IsNullOrWhiteSpace(value) || bounds is null) return null;
            float? score = confidence is not null && confidence(word, out float reading) == 0 && reading is >= 0 and <= 1
                ? reading : null;
            words[index] = new(value, bounds.Value, score);
        }
        return words;
    }

    private static OcrBounds? ReadBounds(long handle, BoundingBox read, Size pixels, Size scaled, Size source)
    {
        // The x64 export returns a result-owned pointer to four (x, y) float pairs. Copy before releasing the result.
        if (handle == 0 || read(handle, out nint data) != 0 || data == 0) return null;
        Span<float> corners = stackalloc float[8];
        for (int i = 0; i < corners.Length; i++)
        {
            float value = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, i * sizeof(float)));
            int limit = i % 2 == 0 ? pixels.Width : pixels.Height;
            if (!float.IsFinite(value) || value < -16 || value > limit + 16) return null;
            corners[i] = value;
        }

        float left = MathF.Min(MathF.Min(corners[0], corners[2]), MathF.Min(corners[4], corners[6]));
        float top = MathF.Min(MathF.Min(corners[1], corners[3]), MathF.Min(corners[5], corners[7]));
        float right = MathF.Max(MathF.Max(corners[0], corners[2]), MathF.Max(corners[4], corners[6]));
        float bottom = MathF.Max(MathF.Max(corners[1], corners[3]), MathF.Max(corners[5], corners[7]));
        float xScale = (float)source.Width / scaled.Width, yScale = (float)source.Height / scaled.Height;
        var bounds = new OcrBounds(Math.Clamp(left, 0, scaled.Width) * xScale, Math.Clamp(top, 0, scaled.Height) * yScale,
            Math.Clamp(right, 0, scaled.Width) * xScale, Math.Clamp(bottom, 0, scaled.Height) * yScale);
        return bounds.IsValid ? bounds : null;
    }
}
