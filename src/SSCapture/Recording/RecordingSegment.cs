using System.Text.Json;
using NAudio.CoreAudioApi;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Recording;

internal sealed class RecordingSegment(string ffmpegPath, string ffprobePath, string workRoot) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Session? session;
    private bool disposed;
    private int recording;
    public bool IsRecording => Volatile.Read(ref recording) != 0;
    public event Action<string>? Failed;

    private sealed class Session(string directory, CaptureSelection selection, AppSettings settings, RecordingClock clock)
    {
        internal string Directory { get; } = directory;
        internal string VideoPath { get; } = Path.Combine(directory, "video.mkv");
        internal string OutputPath { get; } = Path.Combine(directory, "output.mp4");
        internal CaptureSelection Selection { get; } = selection;
        internal AppSettings Settings { get; } = settings;
        internal RecordingClock Clock { get; } = clock;
        internal List<AudioTrack> Audio { get; } = [];
        internal MediaProcess? Video;
        internal string? Mask;
        internal int Stopping;
        internal Exception? Failure;
    }

    public static IReadOnlyList<MicrophoneInfo> GetMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<MicrophoneInfo>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (endpoint) result.Add(new(endpoint.ID, endpoint.FriendlyName));
        }
        return result;
    }

    internal static IReadOnlyList<bool> RequestedAudioSources(AppSettings settings)
    {
        var sources = new List<bool>();
        if (settings.RecordSystemAudio) sources.Add(true);
        if (settings.RecordMicrophone) sources.Add(false);
        return sources;
    }

    public async Task StartAsync(CaptureSelection selection, AppSettings settings, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? created = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session is not null) throw new InvalidOperationException("録画中です。");
            RecordingMedia.ValidateBounds(selection.Bounds);
            if (selection.Polygon is { Count: < 3 }) throw new ArgumentException("自由形状の領域が不正です。");
            if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath)) throw new FileNotFoundException("FFmpeg / ffprobe が見つかりません。setup-media.ps1 を実行してください。");
            var directory = Path.Combine(Path.GetFullPath(workRoot), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            created = new Session(directory, selection with { Polygon = selection.Polygon?.ToArray() }, settings with { }, RecordingClock.Start());
            session = created;
            var current = created;
            created.Mask = RecordingMedia.CreateMask(created.Selection, directory);
            // No device API is touched when both audio switches are off.
            await Task.Run(async () =>
            {
                foreach (var loopback in RequestedAudioSources(current.Settings))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var audio = AudioTrack.Create(loopback, current.Settings.MicrophoneDeviceId,
                        Path.Combine(directory, loopback ? "system.wav" : "microphone.wav"), current.Clock);
                    current.Audio.Add(audio);
                    audio.Failed += error => QueueFailure(current, error);
                    await audio.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            created.Video = new MediaProcess(ffmpegPath, RecordingMedia.CaptureArguments(selection.Bounds, created.VideoPath, created.Settings.IncludeCursor));
            var ready = await Task.WhenAny(created.Video.FirstFrame.Task, created.Video.Exit)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (ready == created.Video.Exit)
            {
                created.Video.EnsureSuccess();
                throw new IOException("映像の録画が開始直後に終了しました。");
            }
            if (Volatile.Read(ref created.Failure) is { } failure) throw failure;
            Volatile.Write(ref recording, 1);
            _ = MonitorVideoAsync(created);
        }
        catch (Exception error)
        {
            if (created is not null)
            {
                await AbortAsync(created).ConfigureAwait(false);
                await WriteDiagnosticsAsync(created, error).ConfigureAwait(false);
                session = null;
                Volatile.Write(ref recording, 0);
                throw new IOException($"{error.Message}\n作業ファイル: {created.Directory}", error);
            }
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task SetAudioEnabledAsync(bool loopback, bool enabled, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AudioTrack? pending = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = session;
            if (current is null || !IsRecording || Volatile.Read(ref current.Stopping) != 0)
                throw new InvalidOperationException("録画していません。");
            cancellationToken.ThrowIfCancellationRequested();
            long changedAt = RecordingClock.Now100ns;
            var existing = current.Audio.SingleOrDefault(track => track.Loopback == loopback);
            if (existing is not null)
            {
                existing.SetEnabled(enabled, changedAt);
                return;
            }
            // An unused OFF source never opens a device. Each source is added
            // at most once, so repeated toggles cannot add amix inputs.
            if (!enabled) return;
            await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var created = AudioTrack.Create(loopback, current.Settings.MicrophoneDeviceId,
                    Path.Combine(current.Directory, loopback ? "system.wav" : "microphone.wav"), current.Clock, changedAt);
                pending = created;
                await created.StartAsync(cancellationToken).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var ready = pending ?? throw new InvalidOperationException("音声トラックを作成できませんでした。");
            ready.RegisterFailureHandler(error => QueueFailure(current, error));
            current.Audio.Add(ready);
            pending = null;
        }
        finally
        {
            // Startup errors/cancellation do not enter the segment Failed path.
            // Stop and release the attempted source while keeping video and all
            // previously committed tracks running.
            if (pending is not null)
            {
                try { await pending.StopAsync(RecordingClock.Now100ns).ConfigureAwait(false); }
                catch { /* Preserve the original startup error; cleanup still owns its worker. */ }
            }
            gate.Release();
        }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var current = session;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is null) throw new InvalidOperationException("録画していません。");
            Interlocked.Exchange(ref current.Stopping, 1);
            Volatile.Write(ref recording, 0);
            var end100ns = RecordingClock.Now100ns;
            // Cleanup is never cancelled midway; the cancellation token applies to the subsequent media conversion.
            var stops = current.Audio.Select(track => track.StopAsync(end100ns)).ToList();
            if (current.Video is not null) stops.Add(current.Video.StopGracefullyAsync(TimeSpan.FromSeconds(8)));
            await Task.WhenAll(stops).ConfigureAwait(false);
            if (current.Failure is { } error) throw error;
            var timing = await RecordingMedia.ProbeVideoTimingAsync(ffprobePath, current.VideoPath, cancellationToken).ConfigureAwait(false);
            var offset = timing.FirstPts - current.Clock.OriginUnixSeconds;
            var elapsed = (end100ns - current.Clock.Origin100ns) / 10_000_000d;
            var clockDrift = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds - current.Clock.UnixSeconds(RecordingClock.Now100ns);
            if (current.Audio.Count > 0 && (Math.Abs(offset) > 60 || Math.Abs(timing.Duration + offset - elapsed) > 3 || Math.Abs(clockDrift) > 0.25))
                throw new InvalidDataException("映像・音声の開始時刻を同期できません（時計の変更または時刻情報の不一致）。作業ファイルは保持しています。");
            var output = await RecordingMedia.MuxAsync(ffmpegPath, ffprobePath, current.VideoPath,
                current.Audio.Select(track => track.Path).ToArray(), current.Mask, offset, timing, current.OutputPath, cancellationToken).ConfigureAwait(false);
            // Only paths explicitly created by this session are removed; output.mp4 is owned by the caller afterwards.
            var cleanupWarnings = new List<string>();
            foreach (var intermediate in new[] { current.VideoPath, current.Mask }.Concat(current.Audio.Select(audio => audio.Path)))
            {
                if (intermediate is null) continue;
                try { File.Delete(intermediate); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                { cleanupWarnings.Add($"{intermediate}: {cleanupError.Message}"); }
            }
            await WriteDiagnosticsAsync(current, null, cleanupWarnings).ConfigureAwait(false);
            return output;
        }
        catch (Exception error)
        {
            if (current is not null)
            {
                await AbortAsync(current).ConfigureAwait(false);
                await WriteDiagnosticsAsync(current, error).ConfigureAwait(false);
                throw new IOException($"{error.Message}\n作業ファイル: {current.Directory}", error);
            }
            throw;
        }
        finally
        {
            if (current?.Video is { } video) await DisposeVideoAsync(video).ConfigureAwait(false);
            session = null;
            Volatile.Write(ref recording, 0);
            gate.Release();
        }
    }

    private async Task MonitorVideoAsync(Session current)
    {
        try
        {
            if (current.Video is null) return;
            await current.Video.Exit.ConfigureAwait(false);
            if (Volatile.Read(ref current.Stopping) == 0)
                QueueFailure(current, new IOException($"FFmpeg の録画が予期せず終了しました。\n{current.Video.Error}"));
        }
        catch (Exception error) { QueueFailure(current, error); }
    }
    private void QueueFailure(Session current, Exception error)
    {
        if (Interlocked.CompareExchange(ref current.Failure, error, null) is null) _ = HandleFailureAsync(current, error);
    }
    private async Task HandleFailureAsync(Session current, Exception error)
    {
        var notify = false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, current) || Volatile.Read(ref current.Stopping) != 0) return;
            await AbortAsync(current).ConfigureAwait(false);
            await WriteDiagnosticsAsync(current, error).ConfigureAwait(false);
            session = null;
            Volatile.Write(ref recording, 0);
            notify = true;
        }
        finally { gate.Release(); }
        if (notify)
        {
            try { Failed?.Invoke($"{error.Message}\n作業ファイル: {current.Directory}"); }
            catch { /* An event subscriber must not tear down the process cleanup task. */ }
        }
    }

    private static async Task AbortAsync(Session current)
    {
        Interlocked.Exchange(ref current.Stopping, 1);
        var end = RecordingClock.Now100ns;
        var cleanup = current.Audio.Select(async audio =>
        {
            try { await audio.StopAsync(end).ConfigureAwait(false); }
            catch { /* Original failure and intermediate audio are retained. */ }
        }).ToList();
        if (current.Video is { } video) cleanup.Add(DisposeVideoAsync(video));
        await Task.WhenAll(cleanup).ConfigureAwait(false);
        current.Video = null;
    }
    private static async Task DisposeVideoAsync(MediaProcess video)
    {
        try { await video.DisposeAsync().ConfigureAwait(false); }
        catch { /* Error reporting must not hide the originating failure. */ }
    }
    private static async Task WriteDiagnosticsAsync(Session current, Exception? error, IReadOnlyList<string>? cleanupWarnings = null)
    {
        try
        {
            var diagnostic = new
            {
                current.Clock, current.Selection.Bounds, Error = error?.ToString(), CleanupWarnings = cleanupWarnings,
                current.Settings.RecordSystemAudio, current.Settings.RecordMicrophone, current.Settings.IncludeCursor,
                Audio = current.Audio.Select(track => track.Diagnostics).ToArray(),
                ClockDriftSeconds = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds - current.Clock.UnixSeconds(RecordingClock.Now100ns)
            };
            await File.WriteAllTextAsync(Path.Combine(current.Directory, "session.json"), JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        }
        catch (Exception) { /* A full disk must not replace the original capture/mux exception. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (session is { } current)
            {
                await AbortAsync(current).ConfigureAwait(false);
                await WriteDiagnosticsAsync(current, new OperationCanceledException("録画中にアプリケーションを終了しました。")).ConfigureAwait(false);
                session = null;
            }
            Volatile.Write(ref recording, 0);
        }
        finally { gate.Release(); }
    }
}
