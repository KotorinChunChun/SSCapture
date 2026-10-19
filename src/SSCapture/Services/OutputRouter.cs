using System.Diagnostics;
using System.Globalization;
using SSCapture.Capture;

namespace SSCapture.Services;

internal sealed record ScreenshotFileName(string? FileName, FileNamePlan? NamePlan, bool OpenFolderAfterSave);

internal sealed class OutputRouter(string workRoot, Func<Bitmap, Task>? copyImage = null,
    Func<string, Task>? copyFile = null, Action<ProcessStartInfo>? launch = null, Action<Bitmap, string?, DateTimeOffset>? captured = null,
    Action<CaptureDelivery>? delivered = null, Action<Bitmap, string?, DateTimeOffset, Rectangle?>? capturedWithBounds = null,
    Func<IReadOnlyList<DisplayTarget>>? readDisplays = null)
{
    private readonly Func<Bitmap, Task> copyImage = copyImage ?? ClipboardWriter.CopyAsync;
    private readonly Func<string, Task> copyFile = copyFile ?? ClipboardWriter.CopyFileAsync;
    private readonly Action<ProcessStartInfo> launchEditor = launch ?? LaunchProcess;
    private readonly Action<string> selectSavedFile = CreateFileSelector(launch);
    private readonly Func<IReadOnlyList<DisplayTarget>> readDisplays = readDisplays ?? InstantCapture.ReadDisplays;

    public async Task<string> ScreenshotAsync(Bitmap image, OutputDestination destination, AppSettings settings, string? fileName = null, FileNamePlan? namePlan = null,
        DateTimeOffset? capturedAt = null, Action? onDelivered = null, Rectangle? captureBounds = null,
        Func<Task<ScreenshotFileName?>>? requestFileName = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        ValidateDestination(destination);
        using var owned = ScreenshotPadding.Apply(image, settings.ScreenshotPaddingPixels);
        DateTimeOffset captureTime = capturedAt ?? namePlan?.CapturedAt ?? DateTimeOffset.Now;
        if (fileName is null && namePlan is null) namePlan = new(settings.FileNamePattern, captureTime, CaptureIntent.Screenshot);
        Func<Task<bool?>>? prepareFile = requestFileName is null ? null : async () =>
        {
            var requested = await requestFileName();
            if (requested is null) return null;
            fileName = requested.FileName;
            namePlan = requested.NamePlan;
            if (fileName is null && namePlan is null) namePlan = new(settings.FileNamePattern, captureTime, CaptureIntent.Screenshot);
            return requested.OpenFolderAfterSave;
        };
        return await DeliverAsync(CaptureIntent.Screenshot, destination, () => copyImage(owned),
            () => SaveAsync(owned, settings.SaveDirectory, fileName, namePlan),
            () => SaveAsync(owned, NewWorkDirectory()), settings.ImageEditorPath, settings.ImageEditorArguments, settings.OpenFolderAfterSave,
            (saved, succeeded, attempted) =>
            {
                if (settings.KeepHistory && attempted)
                {
                    captured?.Invoke(owned, saved, captureTime);
                    capturedWithBounds?.Invoke(owned, saved, captureTime, captureBounds);
                }
                if (succeeded) onDelivered?.Invoke();
            }, captureBounds, prepareFile, DescribeCapture(captureBounds));
    }

    public async Task<string> RecordingAsync(string source, OutputDestination destination, AppSettings settings, string? fileName = null, FileNamePlan? namePlan = null,
        Rectangle? captureBounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(settings);
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("動画ファイルがありません。", source);
        ValidateDestination(destination);
        if (fileName is null && namePlan is null) namePlan = new(settings.FileNamePattern, DateTimeOffset.Now, CaptureIntent.Recording);
        return await DeliverAsync(CaptureIntent.Recording, destination, () => copyFile(source),
            () => Task.Run(() => CopyVideo(source, settings.SaveDirectory, fileName: fileName, namePlan: namePlan)),
            () => Task.FromResult(source), settings.VideoEditorPath, settings.VideoEditorArguments, settings.OpenFolderAfterSave,
            captureBounds: captureBounds, captureDescription: DescribeCapture(captureBounds));
    }

    private async Task<string> DeliverAsync(CaptureIntent intent, OutputDestination destination, Func<Task> copy,
        Func<Task<string>> save, Func<Task<string>> prepareExternal, string executable, string arguments, bool openFolderAfterSave,
        Action<string?, bool, bool>? completed = null, Rectangle? captureBounds = null, Func<Task<bool?>>? prepareFile = null,
        string? captureDescription = null)
    {
        var messages = new List<string>();
        var errors = new List<Exception>();
        string? saved = null;
        OutputDestination succeeded = OutputDestination.None, failed = OutputDestination.None;
        bool folderOpenFailed = false, skipExternal = false;
        if (destination.HasFlag(OutputDestination.Clipboard))
        {
            try { await copy(); succeeded |= OutputDestination.Clipboard; messages.Add("コピーしました。"); }
            catch (Exception ex) { failed |= OutputDestination.Clipboard; errors.Add(ex); messages.Add("コピー失敗: " + ex.Message); }
        }
        if (destination.HasFlag(OutputDestination.File))
        {
            bool shouldSave = true;
            if (prepareFile is not null)
            {
                try
                {
                    // Copy is already complete. Never re-copy after the user confirms or cancels naming.
                    bool? requestedFolderOpen = await prepareFile();
                    if (requestedFolderOpen is null)
                    {
                        shouldSave = false;
                        skipExternal = true;
                        messages.Add("ファイル保存をキャンセルしました。");
                    }
                    else openFolderAfterSave = requestedFolderOpen.Value;
                }
                catch (Exception ex)
                {
                    shouldSave = false;
                    skipExternal = true;
                    failed |= OutputDestination.File;
                    errors.Add(ex);
                    messages.Add("保存失敗: " + ex.Message);
                }
            }
            if (shouldSave)
            {
                try { saved = await save(); succeeded |= OutputDestination.File; messages.Add("保存しました: " + saved); }
                catch (Exception ex) { failed |= OutputDestination.File; errors.Add(ex); messages.Add("保存失敗: " + ex.Message); }
            }
        }
        if (destination.HasFlag(OutputDestination.ExternalApp) && !skipExternal)
        {
            try
            {
                string resolvedExecutable = EditorExecutable.Resolve(executable);
                EditorArguments.Validate(arguments);
                if (destination.HasFlag(OutputDestination.File) && saved is null)
                    throw new IOException("保存に失敗したため、編集アプリへ保存ファイルを渡せません。");
                string path = saved ?? await prepareExternal();
                LaunchEditor(resolvedExecutable, path, arguments);
                succeeded |= OutputDestination.ExternalApp;
                messages.Add("外部アプリに送りました。");
            }
            catch (Exception ex) { failed |= OutputDestination.ExternalApp; errors.Add(ex); messages.Add("外部アプリ失敗: " + ex.Message); }
        }
        if (openFolderAfterSave && saved is not null)
        {
            try { selectSavedFile(saved); }
            catch (Exception ex) { folderOpenFailed = true; errors.Add(ex); messages.Add("保存済みですがフォルダを開けません: " + ex.Message); }
        }
        string result = messages.Count == 0 ? "出力先がすべてOFFです。" : string.Join(Environment.NewLine, messages);
        bool attempted = succeeded != OutputDestination.None || failed != OutputDestination.None;
        try { completed?.Invoke(saved, succeeded != OutputDestination.None, attempted); }
        finally
        {
            if (attempted)
                NotifyDelivered(new CaptureDelivery(intent, succeeded, failed, folderOpenFailed,
                    succeeded.HasFlag(OutputDestination.File) ? saved : null, captureBounds, captureDescription));
        }
        if (errors.Count > 0) throw new IOException(result, new AggregateException(errors));
        return result;
    }

    private string? DescribeCapture(Rectangle? bounds)
    {
        if (bounds is not Rectangle capture) return null;
        try { return CaptureTarget.Describe(capture, readDisplays()); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { return null; }
    }

    private void NotifyDelivered(CaptureDelivery result)
    {
        try { delivered?.Invoke(result); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            // This optional observer only displays completion UI/plays a sound. Its
            // non-fatal failures must not change completed output or replace its error.
            // Restrict isolation to this observer; capture/storage errors keep propagating.
        }
    }

    private static Action<string> CreateFileSelector(Action<ProcessStartInfo>? launch) => launch is null
        ? path => ExplorerSelection.Open(path)
        : path => launch(ExplorerSelection.StartInfo(path));

    private static void ValidateDestination(OutputDestination value)
    {
        if ((value & ~(OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp)) != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private string NewWorkDirectory() => Path.Combine(workRoot, Guid.NewGuid().ToString("N"));

    private static async Task<string> SaveAsync(Bitmap image, string folder, string? fileName = null, FileNamePlan? namePlan = null)
    {
        var copy = (Bitmap)image.Clone();
        return await Task.Run(() => { using (copy) return CaptureStorage.SavePng(copy, folder, DateTimeOffset.Now, fileName, namePlan); });
    }

    internal static string CopyVideo(string source, string folder, DateTimeOffset? capturedAt = null, string? fileName = null, FileNamePlan? namePlan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string directory = Path.GetFullPath(folder);
        // Hold a read-only source handle until publication, never rename or delete the
        // original recording. Opening first also avoids creating folders for missing input.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        fileName = namePlan?.Candidate(0) ?? fileName ?? (capturedAt ?? DateTimeOffset.Now).ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".mp4";
        FileNamePattern.ValidateFileName(fileName);
        if (!fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("動画の拡張子は.mp4です。");
        string stem = fileName[..^4];
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                input.CopyTo(output);
                output.Flush(true);
            }
            for (int n = 0; ; n++)
            {
                string candidate = namePlan?.Candidate(n) ?? stem + (n == 0 ? "" : $"_{n:D4}") + ".mp4";
                FileNamePattern.ValidateFileName(candidate);
                string target = Path.Combine(directory, candidate);
                // Publish one complete copy atomically, even if other saves race for
                // the same name. A collision does not recopy large video bytes.
                try
                {
                    File.Move(temporary, target, false);
                    ownsTemporary = false;
                    return target;
                }
                catch (IOException) when (File.Exists(target)) { }
            }
        }
        finally
        {
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void ValidateEditor(string executable)
    {
        _ = EditorExecutable.Resolve(executable);
    }

    internal static ProcessStartInfo EditorStartInfo(string executable, string path, string arguments = EditorArguments.Default)
    {
        executable = EditorExecutable.Resolve(executable);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in EditorArguments.Expand(arguments, path)) info.ArgumentList.Add(argument);
        return info;
    }

    private void LaunchEditor(string executable, string path, string arguments)
    {
        try { launchEditor(EditorStartInfo(executable, path, arguments)); }
        catch (Exception ex)
        {
            throw new IOException($"外部アプリを起動できません。ファイルは保持されています: {Path.GetFullPath(path)}", ex);
        }
    }

    private static void LaunchProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new IOException("外部アプリのプロセスを開始できません。");
    }
}
