using System.Diagnostics;
using System.Globalization;
using SSCapture.Capture;

namespace SSCapture.Services;

internal sealed record ScreenshotFileName(string? FileName, FileNamePlan? NamePlan, bool OpenFolderAfterSave, string? OcrSeparator = null);

internal sealed partial class OutputRouter(string workRoot, Func<Bitmap, Task>? copyImage = null,
    Func<string, Task>? copyFile = null, Action<ProcessStartInfo>? launch = null, Action<Bitmap, string?, DateTimeOffset>? captured = null,
    Action<CaptureDelivery>? delivered = null, Action<Bitmap, string?, DateTimeOffset, Rectangle?>? capturedWithBounds = null,
    Func<IReadOnlyList<DisplayTarget>>? readDisplays = null, Action? retryChanged = null,
    long retryByteLimit = 256L * 1024 * 1024, Func<string, Task>? beforeMetadataSave = null,
    Action<long, LastCaptureRecipe?>? captureSucceeded = null, Func<IReadOnlyList<DisplayLayout>>? readCaptureLayout = null,
    Action<Bitmap, Rectangle, AppSettings>? pinImage = null, ExternalEditor? externalEditor = null,
    Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognizeForHandoff = null) : IDisposable
{
    private long captureSequence;
    private readonly Func<Bitmap, Task> copyImage = copyImage ?? ClipboardWriter.CopyAsync;
    private readonly Func<string, Task> copyFile = copyFile ?? ClipboardWriter.CopyFileAsync;
    private readonly ExternalEditor editor = externalEditor ?? new(launch);
    private readonly Action<string> selectSavedFile = CreateFileSelector(launch);
    private readonly Func<IReadOnlyList<DisplayTarget>> readDisplays = readDisplays ?? InstantCapture.ReadDisplays;

    public async Task<string> ScreenshotAsync(Bitmap image, OutputDestination destination, AppSettings settings, string? fileName = null, FileNamePlan? namePlan = null,
        DateTimeOffset? capturedAt = null, Action? onDelivered = null, Rectangle? captureBounds = null,
        Func<Task<ScreenshotFileName?>>? requestFileName = null,
        Func<Bitmap, Task<ScreenshotFileName?>>? requestFileNameForImage = null, CaptureMetadata? metadata = null,
        IReadOnlyList<Point>? capturePolygon = null, string? windowTitle = null, CaptureReplaySource? replaySource = null,
        bool useSourceForNaming = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.ResolveEditors();
        Rectangle imageBounds = captureBounds ?? new Rectangle(Point.Empty, image.Size);
        long sequence = Interlocked.Increment(ref captureSequence);
        LastCaptureRecipe? recipe = null;
        if (captureSucceeded is not null && captureBounds is Rectangle recordedBounds)
        {
            try { recipe = LastCaptureRecipe.Create(sequence, recordedBounds, capturePolygon, settings with { DefaultDestination = destination },
                replaySource is CaptureReplaySource.Windows or CaptureReplaySource.Control or CaptureReplaySource.ControlGroup ? [] : (readCaptureLayout ?? RegionCoordinates.ReadDisplays)(), replaySource); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { /* 再撮影できない最新範囲は成功時に無効化し、旧範囲と取り違えない。 */ }
        }
        ValidateDestination(destination);
        if (destination.HasFlag(OutputDestination.File) && settings.MetadataMode != CaptureMetadataMode.None && metadata is null)
            throw new ArgumentException("撮影情報がありません。情報を取得した撮影結果を指定してください。", nameof(metadata));
        WindowCrop crop = replaySource is CaptureReplaySource.Windows { IsSingleWindowSelection: true }
            ? settings.WindowCrop : default;
        Rectangle outputBounds = imageBounds;
        using var cropped = crop.IsEmpty ? null : crop.Apply(image);
        if (cropped is not null)
        {
            outputBounds = new(checked(imageBounds.X + crop.Left), checked(imageBounds.Y + crop.Top), cropped.Width, cropped.Height);
            captureBounds = outputBounds;
            metadata = metadata?.WithCrop(outputBounds);
        }
        var owned = ScreenshotPadding.Apply(cropped ?? image, settings.ScreenshotPaddingPixels);
        if (settings.ScreenshotColorDepth != ScreenshotColorDepth.Original)
        {
            var padded = owned;
            try { owned = await Task.Run(() => ScreenshotColors.Apply(padded, settings.ScreenshotColorDepth)); }
            finally { padded.Dispose(); }
        }
        DateTimeOffset captureTime = capturedAt ?? namePlan?.CapturedAt ?? DateTimeOffset.Now;
        FileNamePlan defaultPlan = namePlan ?? new(settings.FileNamePattern, captureTime, CaptureIntent.Screenshot);
        var naming = new HandoffFileName(defaultPlan, namePlan is null ? fileName : null, settings.OneOcrDirectory, recognize: recognizeForHandoff);
        var retry = NewRetry(owned);
        if (cropped is not null && (requestFileNameForImage is not null || naming.NeedsOcr) ||
            useSourceForNaming && requestFileNameForImage is not null &&
            (settings.ScreenshotPaddingPixels != 0 || settings.ScreenshotColorDepth != ScreenshotColorDepth.Original))
            retry.NamingImage = (Bitmap)(cropped ?? image).Clone();
        metadata = metadata?.WithPadding(settings.ScreenshotPaddingPixels);
        CaptureMetadataMode metadataMode = destination.HasFlag(OutputDestination.File) ? settings.MetadataMode : CaptureMetadataMode.None;
        MetadataSidecar? sidecar = metadata is not null && metadataMode.HasFlag(CaptureMetadataMode.Json) ? new(metadata) : null;
        bool fileCancelled = false;
        Func<Task<bool?>>? prepareFile = requestFileName is null && requestFileNameForImage is null ? null : async () =>
        {
            var requested = requestFileNameForImage is not null ? await requestFileNameForImage(retry.NamingImage ?? owned) : await requestFileName!();
            if (requested is null) { fileCancelled = true; return null; }
            if (!retry.IsRetry) recipe?.ConfirmNaming(requested);
            naming = new(requested.NamePlan ?? defaultPlan, requested.NamePlan is null ? requested.FileName : null, settings.OneOcrDirectory,
                confirmed: requested.NamePlan is not null, recognize: recognizeForHandoff);
            if (!naming.NeedsOcr) retry.ReleaseNamingImage();
            return requested.OpenFolderAfterSave;
        };
        retry.Pending = destination;
        retry.PrepareFile = prepareFile;
        retry.Execute = () => DeliverAsync(CaptureIntent.Screenshot, retry.Pending, () => copyImage(owned),
            async () =>
            {
                FileNamePlan? plan = await naming.ResolveAsync(retry.NamingImage ?? owned);
                return await SaveAsync(owned, settings.SaveDirectory, naming.FileName, plan, metadata, metadataMode,
                    sidecar is null ? null : sidecar.RememberImage);
            },
            async () =>
            {
                using var pending = new NotificationImage(owned, TemporaryHandoff.DirectoryFor(workRoot), naming, retry.NamingImage);
                return await pending.PrepareForHandoffAsync();
            }, settings.EditorFor(CaptureIntent.Screenshot), settings.OpenFolderAfterSave,
            (saved, succeeded, attempted) =>
            {
                if (!retry.IsRetry && settings.KeepHistory && attempted)
                {
                    captured?.Invoke(owned, saved, captureTime);
                    capturedWithBounds?.Invoke(owned, saved, captureTime, captureBounds);
                }
                if (succeeded && !retry.SuccessNotified)
                {
                    retry.SuccessNotified = true;
                    if (!retry.IsRetry && !fileCancelled) captureSucceeded?.Invoke(sequence, recipe);
                    onDelivered?.Invoke();
                }
            }, captureBounds, retry.PrepareFile, string.IsNullOrWhiteSpace(windowTitle)
                ? DescribeCapture(captureBounds, (replaySource as CaptureReplaySource.Monitors)?.Bounds) : $"ウィンドウ「{windowTitle}」" + (crop.IsEmpty ? "" : $"【{outputBounds.X},{outputBounds.Y} / {outputBounds.Width}×{outputBounds.Height}px】"), retry,
            sidecar is null ? null : async path => { if (beforeMetadataSave is not null) await beforeMetadataSave(path); await Task.Run(() => sidecar.Write(path)); },
            settings.ShowCaptureNotification
                ? () => new NotificationImage(owned, TemporaryHandoff.DirectoryFor(workRoot), naming, naming.NeedsOcr ? retry.NamingImage : null) : null,
            captureSettings: settings with { DefaultDestination = destination },
            pin: () =>
            {
                if (pinImage is null) throw new InvalidOperationException("ピン留め表示を開始できません。");
                pinImage(owned, outputBounds, settings);
            });
        return await ExecuteRetryJobAsync(retry);
    }

    public async Task<string> RecordingAsync(string source, OutputDestination destination, AppSettings settings, string? fileName = null, FileNamePlan? namePlan = null,
        Rectangle? captureBounds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(settings);
        source = Path.GetFullPath(source);
        settings = settings.ResolveEditors();
        if (!File.Exists(source)) throw new FileNotFoundException("動画ファイルがありません。", source);
        ValidateDestination(destination);
        if (destination.HasFlag(OutputDestination.Pinned))
        {
            destination &= ~OutputDestination.Pinned;
            if (destination == OutputDestination.None) throw new ArgumentException("ピン留め表示は静止画専用です。録画の出力先を選択してください。");
        }
        namePlan ??= fileName is null ? new(settings.FileNamePattern, DateTimeOffset.Now, CaptureIntent.Recording,
            FileNamePattern.UsesOcr(settings.FileNamePattern) ? "OCRなし" : "") : null;
        string cache = TemporaryHandoff.DirectoryFor(workRoot);
        Task<TemporaryHandoff> PrepareVideo() => Task.Run(() => TemporaryHandoff.Publish(cache,
            publish => CopyVideo(source, cache, fileName: fileName, namePlan: HandoffFileName.CachePlan(namePlan), beforePublish: publish)));
        try
        {
            return await DeliverAsync(CaptureIntent.Recording, destination, () => copyFile(source),
                () => Task.Run(() => CopyVideo(source, settings.SaveDirectory, fileName: fileName, namePlan: namePlan)),
                PrepareVideo, settings.EditorFor(CaptureIntent.Recording), settings.OpenFolderAfterSave,
                captureBounds: captureBounds, captureDescription: DescribeCapture(captureBounds),
                notificationPath: settings.ShowCaptureNotification ? source : null,
                prepareNotificationFile: settings.ShowCaptureNotification ? PrepareVideo : null,
                captureSettings: settings with { DefaultDestination = destination });
        }
        catch (IOException ex) when (destination.HasFlag(OutputDestination.ExternalApp))
        {
            throw new IOException(ex.Message + Environment.NewLine + "録画原本は保持されています: " + source, ex);
        }
    }

    private async Task<string> DeliverAsync(CaptureIntent intent, OutputDestination destination, Func<Task> copy,
        Func<Task<string>> save, Func<Task<TemporaryHandoff>> prepareExternal, ExternalApplication? application, bool openFolderAfterSave,
        Action<string?, bool, bool>? completed = null, Rectangle? captureBounds = null, Func<Task<bool?>>? prepareFile = null,
        string? captureDescription = null, RetryJob? retry = null, Func<string, Task>? saveMetadata = null,
        Func<NotificationImage>? notificationImage = null, string? notificationPath = null, AppSettings? captureSettings = null,
        Action? pin = null, Func<Task<TemporaryHandoff>>? prepareNotificationFile = null)
    {
        var messages = new List<string>();
        var errors = new List<Exception>();
        string? saved = retry?.SavedPath;
        if (retry?.IsRetry == true)
            openFolderAfterSave = retry.FolderPending || destination.HasFlag(OutputDestination.File) && openFolderAfterSave;
        OutputDestination succeeded = OutputDestination.None, failed = OutputDestination.None;
        bool folderOpenFailed = false, skipExternal = false;
        bool metadataFailed = false;
        if (destination.HasFlag(OutputDestination.Clipboard))
        {
            try { await copy(); succeeded |= OutputDestination.Clipboard; messages.Add("コピーしました。"); }
            catch (Exception ex) { failed |= OutputDestination.Clipboard; errors.Add(ex); messages.Add("コピー失敗: " + ex.Message); }
        }
        if (destination.HasFlag(OutputDestination.Pinned))
        {
            try { (pin ?? throw new InvalidOperationException("ピン留め表示を開始できません。"))(); succeeded |= OutputDestination.Pinned; messages.Add("ピン留めしました。"); }
            catch (Exception ex) { failed |= OutputDestination.Pinned; errors.Add(ex); messages.Add("ピン留め失敗: " + ex.Message); }
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
                    if (retry is not null) retry.PrepareFile = null;
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
        if (saveMetadata is not null && saved is not null && (succeeded.HasFlag(OutputDestination.File) || retry?.MetadataPending == true))
        {
            try { await saveMetadata(saved); if (retry is not null) retry.MetadataPending = false; messages.Add("撮影情報JSONを保存しました。"); }
            catch (Exception ex) { metadataFailed = true; if (retry is not null) retry.MetadataPending = true; errors.Add(ex); messages.Add("画像は保存済みですが撮影情報JSONを保存できません: " + ex.Message); }
        }
        if (destination.HasFlag(OutputDestination.ExternalApp) && !skipExternal)
        {
            try
            {
                var open = editor.Resolve(application);
                if (destination.HasFlag(OutputDestination.File) && saved is null)
                    throw new IOException("保存に失敗したため、編集アプリへ保存ファイルを渡せません。");
                TemporaryHandoff? handoff = null;
                try
                {
                    if (saved is null)
                    {
                        handoff = retry?.ExternalFile;
                        if (handoff?.TryRenew() != true)
                        {
                            handoff?.Dispose();
                            handoff = await prepareExternal();
                            if (retry is not null) retry.ExternalFile = handoff;
                        }
                    }
                    open(saved ?? handoff!.Path);
                }
                finally { handoff?.Dispose(); }
                succeeded |= OutputDestination.ExternalApp;
                messages.Add("外部アプリに送りました。");
            }
            catch (Exception ex) { failed |= OutputDestination.ExternalApp; errors.Add(ex); messages.Add("外部アプリ失敗: " + ex.Message); }
        }
        if (openFolderAfterSave && saved is not null)
        {
            try { selectSavedFile(saved); if (messages.Count == 0) messages.Add("保存フォルダを開きました。"); }
            catch (Exception ex) { folderOpenFailed = true; errors.Add(ex); messages.Add("保存済みですがフォルダを開けません: " + ex.Message); }
        }
        string result = messages.Count == 0 ? "出力先がすべてOFFです。" : string.Join(Environment.NewLine, messages);
        bool attempted = succeeded != OutputDestination.None || failed != OutputDestination.None;
        if (retry is not null) { retry.Pending = failed; retry.FolderPending = folderOpenFailed; retry.SavedPath = saved; }
        try { completed?.Invoke(saved, succeeded != OutputDestination.None, attempted); }
        finally
        {
            if (attempted || metadataFailed || folderOpenFailed || openFolderAfterSave && saved is not null)
            {
                if (saved is null && notificationImage is not null) notificationPath ??= retry?.ExternalFile?.Path;
                NotifyDelivered(new CaptureDelivery(intent, succeeded, failed, folderOpenFailed,
                    succeeded.HasFlag(OutputDestination.File) ? saved : null, captureBounds, captureDescription, metadataFailed,
                    saved is null ? notificationPath : null, notificationImage?.Invoke(), captureSettings,
                    retry is { Failed: true } ? retry.Serial : null, saved is null ? prepareNotificationFile : null));
            }
        }
        if (errors.Count > 0) throw new IOException(result, new AggregateException(errors));
        return result;
    }

    private string? DescribeCapture(Rectangle? bounds, IReadOnlyList<Rectangle>? capturedMonitors = null)
    {
        if (bounds is not Rectangle capture) return null;
        try { return CaptureTarget.Describe(capture, readDisplays(), capturedMonitors); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { return null; }
    }

    private void NotifyDelivered(CaptureDelivery result)
    {
        try { if (delivered is null) result.NotificationImage?.Dispose(); else delivered(result); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            result.NotificationImage?.Dispose();
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
        if ((value & ~(OutputDestination.Clipboard | OutputDestination.File | OutputDestination.ExternalApp | OutputDestination.Pinned)) != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static async Task<string> SaveAsync(Bitmap image, string folder, string? fileName = null, FileNamePlan? namePlan = null,
        CaptureMetadata? metadata = null, CaptureMetadataMode metadataMode = CaptureMetadataMode.None, Action<string>? prepared = null)
    {
        var copy = (Bitmap)image.Clone();
        return await Task.Run(() => { using (copy) return CaptureStorage.SavePng(copy, folder, DateTimeOffset.Now, fileName, namePlan, metadata, metadataMode, prepared); });
    }

    internal static string CopyVideo(string source, string folder, DateTimeOffset? capturedAt = null, string? fileName = null, FileNamePlan? namePlan = null,
        Action<string, string>? beforePublish = null)
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
                    if (Path.Exists(target)) continue;
                    beforePublish?.Invoke(temporary, target);
                    File.Move(temporary, target, false);
                    ownsTemporary = false;
                    return target;
                }
                catch (IOException) when (Path.Exists(target)) { }
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

}
