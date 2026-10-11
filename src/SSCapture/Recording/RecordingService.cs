using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Recording;

// The small boundary permits state-transition tests without opening a display or audio device.
internal interface IRecordingSegment : IAsyncDisposable
{
    event Action<string>? Failed;
    Task StartAsync(CaptureSelection selection, AppSettings settings, CancellationToken cancellationToken = default);
    Task<string> StopAsync(CancellationToken cancellationToken = default);
    Task SetAudioEnabledAsync(bool loopback, bool enabled, CancellationToken cancellationToken = default);
}

internal sealed class RecordingService : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string workRoot;
    private readonly Func<string, IRecordingSegment> createSegment;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<string>> concatenate;
    private Session? session;
    private bool disposed;
    private int recording, paused, systemAudio, microphone;

    public bool IsRecording => Volatile.Read(ref recording) != 0;
    public bool IsPaused => Volatile.Read(ref paused) != 0;
    public bool SystemAudioEnabled => Volatile.Read(ref systemAudio) != 0;
    public bool MicrophoneEnabled => Volatile.Read(ref microphone) != 0;
    public event Action<string>? Failed;

    private sealed class Session(string directory, CaptureSelection selection, AppSettings settings)
    {
        internal string Directory { get; } = directory;
        internal CaptureSelection Selection { get; } = selection;
        internal AppSettings Settings = settings;
        internal List<string> Parts { get; } = [];
        internal IRecordingSegment? Active;
        internal Action<string>? FailureHandler;
    }

    private sealed class NativeSegment(RecordingSegment segment) : IRecordingSegment
    {
        public event Action<string>? Failed { add => segment.Failed += value; remove => segment.Failed -= value; }
        public Task StartAsync(CaptureSelection selection, AppSettings settings, CancellationToken cancellationToken = default)
            => segment.StartAsync(selection, settings, cancellationToken);
        public Task<string> StopAsync(CancellationToken cancellationToken = default) => segment.StopAsync(cancellationToken);
        public Task SetAudioEnabledAsync(bool loopback, bool enabled, CancellationToken cancellationToken = default)
            => segment.SetAudioEnabledAsync(loopback, enabled, cancellationToken);
        public ValueTask DisposeAsync() => segment.DisposeAsync();
    }

    public RecordingService(string ffmpegPath, string ffprobePath, string workRoot)
        : this(workRoot, directory => new NativeSegment(new RecordingSegment(ffmpegPath, ffprobePath, directory)),
            (parts, output, cancellationToken) => RecordingMedia.ConcatAsync(ffmpegPath, ffprobePath, parts, output, cancellationToken)) { }

    internal RecordingService(string workRoot, Func<string, IRecordingSegment> createSegment,
        Func<IReadOnlyList<string>, string, CancellationToken, Task<string>> concatenate)
    {
        this.workRoot = Path.GetFullPath(workRoot);
        this.createSegment = createSegment;
        this.concatenate = concatenate;
    }

    public static IReadOnlyList<MicrophoneInfo> GetMicrophones() => RecordingSegment.GetMicrophones();
    internal static IReadOnlyList<bool> RequestedAudioSources(AppSettings settings) => RecordingSegment.RequestedAudioSources(settings);

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
            created = new Session(Path.Combine(workRoot, "recording-" + Guid.NewGuid().ToString("N")),
                selection with { Polygon = selection.Polygon?.ToArray() }, settings with { });
            Directory.CreateDirectory(created.Directory);
            session = created;
            await BeginSegmentAsync(created, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref systemAudio, settings.RecordSystemAudio ? 1 : 0);
            Volatile.Write(ref microphone, settings.RecordMicrophone ? 1 : 0);
            Volatile.Write(ref paused, 0);
            Volatile.Write(ref recording, 1);
        }
        catch
        {
            if (created is not null) await EndSessionAsync(created).ConfigureAwait(false);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? current = null;
        try
        {
            current = RequireSession();
            if (current.Active is null) return;
            await FinishSegmentAsync(current, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref paused, 1);
        }
        catch (Exception error) when (current is not null)
        {
            await EndSessionAsync(current).ConfigureAwait(false);
            throw new IOException($"{error.Message}\n作業ファイル: {current.Directory}", error);
        }
        finally { gate.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? current = null;
        try
        {
            current = RequireSession();
            if (current.Active is not null) return;
            await BeginSegmentAsync(current, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref paused, 0);
        }
        catch (Exception error) when (current is not null)
        {
            await EndSessionAsync(current).ConfigureAwait(false);
            throw new IOException($"{error.Message}\n作業ファイル: {current.Directory}", error);
        }
        finally { gate.Release(); }
    }

    public Task SetSystemAudioAsync(bool enabled, CancellationToken cancellationToken = default)
        => SetAudioAsync(true, enabled, cancellationToken);
    public Task SetMicrophoneAsync(bool enabled, CancellationToken cancellationToken = default)
        => SetAudioAsync(false, enabled, cancellationToken);

    private async Task SetAudioAsync(bool loopback, bool enabled, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = RequireSession();
            if ((loopback ? current.Settings.RecordSystemAudio : current.Settings.RecordMicrophone) == enabled) return;
            // Commit only after device initialization succeeds. Paused sessions defer initialization until resume.
            if (current.Active is { } active) await active.SetAudioEnabledAsync(loopback, enabled, cancellationToken).ConfigureAwait(false);
            current.Settings = loopback ? current.Settings with { RecordSystemAudio = enabled } : current.Settings with { RecordMicrophone = enabled };
            if (loopback) Volatile.Write(ref systemAudio, enabled ? 1 : 0);
            else Volatile.Write(ref microphone, enabled ? 1 : 0);
        }
        finally { gate.Release(); }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? current = null;
        try
        {
            current = RequireSession();
            if (current.Active is not null) await FinishSegmentAsync(current, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref recording, 0);
            Volatile.Write(ref paused, 0);
            if (current.Parts.Count == 0) throw new InvalidDataException("録画区間がありません。");
            // A single segment is already validated by RecordingSegment and needs no second conversion.
            return current.Parts.Count == 1 ? current.Parts[0] : await concatenate(current.Parts.ToArray(),
                Path.Combine(current.Directory, "output.mp4"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (current is not null)
        {
            throw new IOException($"{error.Message}\n作業ファイル: {current.Directory}", error);
        }
        finally
        {
            if (current is not null) await EndSessionAsync(current).ConfigureAwait(false);
            gate.Release();
        }
    }

    private Session RequireSession()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return session ?? throw new InvalidOperationException("録画していません。");
    }

    private async Task BeginSegmentAsync(Session current, CancellationToken cancellationToken)
    {
        var part = createSegment(current.Directory);
        current.Active = part;
        current.FailureHandler = message => _ = HandleFailureAsync(current, part, message);
        part.Failed += current.FailureHandler;
        await part.StartAsync(current.Selection, current.Settings, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishSegmentAsync(Session current, CancellationToken cancellationToken)
    {
        var part = current.Active!;
        // Do not discard an active failed part until the abort path has disposed its acquisition resources.
        string path = await part.StopAsync(cancellationToken).ConfigureAwait(false);
        current.Parts.Add(path);
        await ReleaseSegmentAsync(current).ConfigureAwait(false);
    }

    private static async Task ReleaseSegmentAsync(Session current)
    {
        var part = current.Active;
        if (part is null) return;
        current.Active = null;
        part.Failed -= current.FailureHandler;
        current.FailureHandler = null;
        // Segment disposal preserves raw/intermediate files and must not hide a capture or conversion error.
        try { await part.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task EndSessionAsync(Session current)
    {
        await ReleaseSegmentAsync(current).ConfigureAwait(false);
        if (!ReferenceEquals(session, current)) return;
        session = null;
        Volatile.Write(ref recording, 0);
        Volatile.Write(ref paused, 0);
    }

    private async Task HandleFailureAsync(Session current, IRecordingSegment part, string message)
    {
        bool notify = false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(session, current) || !ReferenceEquals(current.Active, part)) return;
            await EndSessionAsync(current).ConfigureAwait(false);
            notify = true;
        }
        finally { gate.Release(); }
        if (notify)
        {
            try { Failed?.Invoke($"{message}\n作業ファイル: {current.Directory}"); }
            catch (Exception) { /* Observers cannot undo acquisition cleanup or fault an unobserved background task. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (session is { } current) await EndSessionAsync(current).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
