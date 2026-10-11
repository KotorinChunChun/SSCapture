using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SSCapture.Recording;

internal sealed record MicrophoneInfo(string Id, string Name);

/// <summary>Copies the short WASAPI callback into a bounded queue; file writes occur on a separate worker.</summary>
internal sealed class AudioTrack : IAsyncDisposable
{
    private readonly MMDevice device;
    private readonly WasapiRecorder recorder;
    private readonly Channel<AudioPacket> packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task writerTask;
    private readonly AudioTimeline timeline;
    private readonly AudioGate audioGate;
    private readonly WaveFileWriter writer;
    private readonly object failureGate = new();
    private Task? cleanup;
    private Exception? failure;
    private long stop100ns;
    private int stopping;
    private bool captureThreadStarted;
    internal string Path { get; }
    internal bool Loopback { get; }
    internal event Action<Exception>? Failed;

    private AudioTrack(MMDevice device, bool loopback, string path, RecordingClock clock, long enabledFrom100ns)
    {
        this.device = device;
        Path = path;
        Loopback = loopback;
        var builder = new WasapiRecorderBuilder().WithDevice(device).WithPollingSync().WithBufferLength(50);
        if (loopback) builder.WithLoopbackCapture();
        recorder = builder.Build();
        WaveFileWriter? createdWriter = null;
        try
        {
            var format = recorder.WaveFormat;
            if (format.SampleRate <= 0 || format.BlockAlign <= 0 || format.BitsPerSample < 16)
                throw new NotSupportedException("この録音デバイスの音声形式は対応していません（16 bit 以上が必要です）。");
            writer = new WaveFileWriter(path, format, new WaveFileWriterOptions { EnableRf64 = true });
            createdWriter = writer;
            audioGate = new AudioGate(format.SampleRate, clock.Origin100ns, enabledFrom100ns);
            timeline = new AudioTimeline(format.SampleRate, format.BlockAlign, clock.Origin100ns, writer.Write, audioGate);
            recorder.DataAvailable += OnData;
            recorder.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null) SetFailure(args.Exception);
                else if (Volatile.Read(ref stopping) == 0) SetFailure(new IOException("音声デバイスの録音が予期せず停止しました。"));
                stopped.TrySetResult();
            };
            writerTask = Task.Run(WritePacketsAsync);
        }
        catch
        {
            try { createdWriter?.Dispose(); }
            finally { recorder.Dispose(); }
            throw;
        }
    }

    internal static AudioTrack Create(bool loopback, string deviceId, string path, RecordingClock clock, long? enabledFrom100ns = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            device = loopback ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) :
                string.IsNullOrWhiteSpace(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active || device.DataFlow != (loopback ? DataFlow.Render : DataFlow.Capture))
                throw new InvalidOperationException(loopback ? "PC 音声の出力デバイスを使用できません。" : "選択したマイクを使用できません。設定で選び直してください。");
            return new AudioTrack(device, loopback, path, clock, enabledFrom100ns ?? clock.Origin100ns);
        }
        catch { device?.Dispose(); throw; }
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        // Constructed and started off the UI thread: RecordingStopped must not depend on the UI message loop.
        recorder.StartRecording();
        captureThreadStarted = true;
        var deadline = Environment.TickCount64 + 5_000;
        while (recorder.CaptureState == CaptureState.Starting)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("音声デバイスを開始できませんでした。");
            await Task.Delay(10).ConfigureAwait(false);
        }
        if (recorder.CaptureState != CaptureState.Capturing)
            throw failure ?? new IOException("音声デバイスの録音開始に失敗しました。");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var received = RecordingClock.Now100ns;
        if (!packets.Writer.TryWrite(audioGate.CapturePacket(buffer, (int)flags, devicePosition, qpcPosition, received)))
            SetFailure(new IOException("音声の保存が追いつかないため録画を停止します。作業ファイルは保持しています。"));
    }

    private void SetFailure(Exception error)
    {
        Action<Exception>? notify;
        lock (failureGate)
        {
            if (failure is not null) return;
            Volatile.Write(ref failure, error);
            notify = Failed;
        }
        notify?.Invoke(error);
    }

    internal void SetEnabled(bool enabled, long timestamp100ns) => audioGate.SetEnabled(enabled, timestamp100ns);

    // Lazy startup is committed only while healthy. A failure before this point
    // belongs to the attempted ON operation, not to the already running video.
    internal void RegisterFailureHandler(Action<Exception> handler)
    {
        lock (failureGate)
        {
            if (failure is { } error) throw new IOException("音声デバイスの録音開始に失敗しました。", error);
            Failed += handler;
        }
    }

    private async Task WritePacketsAsync()
    {
        try
        {
            await foreach (var packet in packets.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var end = Volatile.Read(ref stop100ns);
                timeline.Append(packet, end > 0 ? end : null);
            }
            timeline.Finish(Volatile.Read(ref stop100ns));
        }
        catch (Exception error) { SetFailure(error); throw; }
        finally { writer.Dispose(); }
    }

    internal async Task StopAsync(long end100ns)
    {
        if (cleanup is null)
        {
            Volatile.Write(ref stop100ns, end100ns);
            Interlocked.Exchange(ref stopping, 1);
            cleanup = CleanupAsync();
        }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        if (failure is { } error) throw new IOException("音声録音に失敗しました。作業ファイルは保持しています。", error);
    }

    private async Task CleanupAsync()
    {
        try
        {
            // Let the final pre-stop samples leave the 50 ms WASAPI buffer. Packets are clipped to the
            // requested stop timestamp by the writer, so this drain does not extend the recording.
            if (captureThreadStarted) await Task.Delay(100).ConfigureAwait(false);
            // Repeated stop handles NAudio's Starting -> Capturing race. This worker remains alive if a driver
            // returns late; the public caller has a bounded wait and reports failure instead of a valid recording.
            while (captureThreadStarted && recorder.CaptureState != CaptureState.Stopped)
            {
                recorder.StopRecording();
                if (stopped.Task.IsCompleted) break;
                await Task.WhenAny(stopped.Task, Task.Delay(20)).ConfigureAwait(false);
            }
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            packets.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); }
            finally { device.Dispose(); }
        }
    }

    internal object Diagnostics => new { Path, timeline.FramesWritten, timeline.TimestampFallbacks, timeline.Discontinuities, timeline.SilenceFrames, timeline.OverlapFrames };
    public async ValueTask DisposeAsync() => await StopAsync(RecordingClock.Now100ns).ConfigureAwait(false);
}
