using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void RecordingSessionTests()
    {
        Run("録画セッション: 一時停止中も存続し再開区間を順番に連結", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            var service = fixture.Service;
            await service.StartAsync(SessionSelection(), new AppSettings());
            Check(service.IsRecording && !service.IsPaused, "開始状態が不正");
            await service.PauseAsync();
            Check(service.IsRecording && service.IsPaused, "一時停止でセッションが終了している");
            Equal(1, fixture.Parts[0].Stops);
            Equal(1, fixture.Parts[0].Disposals);
            await service.ResumeAsync();
            Check(service.IsRecording && !service.IsPaused, "再開状態が不正");
            Equal(2, fixture.Parts.Count);
            string output = await service.StopAsync();
            Equal(1, fixture.ConcatCalls);
            Check(fixture.Combined.SequenceEqual(fixture.Parts.Select(part => part.Path)), "区間の順番が異なる");
            Equal(Path.Combine(fixture.SessionDirectory!, "output.mp4"), output);
            Check(!service.IsRecording && !service.IsPaused, "終了後も録画中");
            Check(fixture.Parts.All(part => part.Disposals == 1), "区間が解放されていない");
        })));

        Run("録画セッション: 一時停止したまま終了しても空区間を作らない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            await fixture.Service.PauseAsync();
            string output = await fixture.Service.StopAsync();
            Equal(1, fixture.Parts.Count);
            Equal(fixture.Parts[0].Path, output);
            Equal(0, fixture.ConcatCalls);
            Equal(1, fixture.Parts[0].Stops);
        })));

        Run("録画セッション: 音声は独立し一時停止中の変更を再開時に適用", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            var original = new AppSettings { RecordSystemAudio = true, RecordMicrophone = false, MicrophoneDeviceId = "synthetic-mic" };
            await fixture.Service.StartAsync(SessionSelection(), original);
            await fixture.Service.SetMicrophoneAsync(true);
            Check(fixture.Service.SystemAudioEnabled && fixture.Service.MicrophoneEnabled, "独立トグルが他方を変更している");
            Equal((false, true), fixture.Parts[0].AudioChanges.Single());
            await fixture.Service.PauseAsync();
            await fixture.Service.SetSystemAudioAsync(false);
            Equal(1, fixture.Parts[0].AudioChanges.Count);
            await fixture.Service.ResumeAsync();
            Check(!fixture.Parts[1].Settings!.RecordSystemAudio && fixture.Parts[1].Settings!.RecordMicrophone, "再開時の音声設定が古い");
            Equal("synthetic-mic", fixture.Parts[1].Settings!.MicrophoneDeviceId);
            Check(original.RecordSystemAudio && !original.RecordMicrophone, "ユーザーの既定設定を変更している");
            await fixture.Service.StopAsync();
        })));

        Run("録画セッション: 初回音声ON失敗はOFFを維持して録画を継続", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings { RecordSystemAudio = true });
            fixture.Parts[0].AudioFailure = new IOException("synthetic device unavailable");
            await ExpectSessionFailureAsync<IOException>(() => fixture.Service.SetMicrophoneAsync(true));
            Check(fixture.Service.IsRecording && !fixture.Service.IsPaused, "切替失敗がセッションを終了している");
            Check(fixture.Service.SystemAudioEnabled && !fixture.Service.MicrophoneEnabled, "切替失敗後の設定が変わっている");
            Equal(0, fixture.Parts[0].Disposals);
            fixture.Parts[0].AudioFailure = null;
            await fixture.Service.SetMicrophoneAsync(true);
            Check(fixture.Service.MicrophoneEnabled, "再試行できない");
            await fixture.Service.StopAsync();
        })));

        Run("録画セッション: 一時停止と再開の連打は区間を重複作成しない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Parts[0].StopWait = stopping.Task;
            var pauses = Enumerable.Range(0, 12).Select(_ => fixture.Service.PauseAsync()).ToArray();
            Check(pauses.All(task => !task.IsCompleted), "停止中に別操作が完了している");
            Equal(1, fixture.Parts[0].Stops);
            stopping.SetResult();
            await Task.WhenAll(pauses);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Service.ResumeAsync()));
            Equal(2, fixture.Parts.Count);
            Check(fixture.Parts.All(part => part.Starts == 1), "再開の連打で区間を二重開始している");
            await fixture.Service.StopAsync();
        })));

        Run("録画セッション: 終了は処理中の音声切替が完了するまで待つ", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            var changing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Parts[0].AudioWait = changing.Task;
            Task change = fixture.Service.SetSystemAudioAsync(true);
            Task<string> stop = fixture.Service.StopAsync();
            Equal(0, fixture.Parts[0].Stops);
            changing.SetResult();
            await change;
            await stop;
            Equal(1, fixture.Parts[0].Stops);
            Check(fixture.Service.SystemAudioEnabled, "成功した切替が反映されていない");
        })));

        Run("録画セッション: 旧区間の遅延障害は無視し現区間の障害は1回だけ通知", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            int notifications = 0;
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Service.Failed += _ => { Interlocked.Increment(ref notifications); notified.TrySetResult(); };
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            Action<string>? oldHandler = fixture.Parts[0].SnapshotFailure();
            await fixture.Service.PauseAsync();
            await fixture.Service.ResumeAsync();
            oldHandler?.Invoke("stale failure");
            Check(fixture.Service.IsRecording, "旧区間の障害で新しい区間が停止した");
            Equal(0, notifications);
            Action<string>? currentHandler = fixture.Parts[1].SnapshotFailure();
            currentHandler?.Invoke("current failure");
            currentHandler?.Invoke("duplicate failure");
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await fixture.Service.DisposeAsync();
            Equal(1, notifications);
            Check(!fixture.Service.IsRecording && !fixture.Service.IsPaused, "障害後も録画状態が残る");
            Equal(1, fixture.Parts[1].Disposals);
            Check(File.Exists(fixture.Parts[0].Path), "完成区間を障害時に削除している");
        })));

        Run("録画セッション: 一時停止の失敗は取得を解放して作業ファイルを保持", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            fixture.Parts[0].StopFailure = new IOException("synthetic mux failure");
            await ExpectSessionFailureAsync<IOException>(() => fixture.Service.PauseAsync());
            Check(!fixture.Service.IsRecording, "一時停止失敗後もセッションが存続している");
            Equal(1, fixture.Parts[0].Disposals);
            Check(File.Exists(fixture.Parts[0].WorkPath), "失敗区間の作業ファイルを削除している");
        })));

        Run("録画セッション: 再開の失敗は前区間を保持して新しい取得を解放", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            await fixture.Service.PauseAsync();
            fixture.Configure = part => part.StartFailure = new IOException("synthetic resume failure");
            await ExpectSessionFailureAsync<IOException>(() => fixture.Service.ResumeAsync());
            Check(!fixture.Service.IsRecording && !fixture.Service.IsPaused, "再開失敗後の状態が不正");
            Check(File.Exists(fixture.Parts[0].Path), "再開失敗で前区間を削除している");
            Equal(1, fixture.Parts[1].Disposals);
        })));

        Run("録画セッション: 連結失敗でも全区間を保持し取得は残さない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory) { ConcatFailure = new IOException("synthetic concat failure") };
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            await fixture.Service.PauseAsync();
            await fixture.Service.ResumeAsync();
            await ExpectSessionFailureAsync<IOException>(() => fixture.Service.StopAsync());
            Check(!fixture.Service.IsRecording && fixture.Parts.All(part => part.Disposals == 1), "連結失敗で取得が残る");
            Check(fixture.Parts.All(part => File.Exists(part.Path)), "連結前区間が消えている");
        })));

        Run("録画セッション: 破棄は取得を止め以後の開始を拒否", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            await using var fixture = new RecordingSessionFixture(directory);
            await fixture.Service.StartAsync(SessionSelection(), new AppSettings());
            await fixture.Service.DisposeAsync();
            await fixture.Service.DisposeAsync();
            Equal(1, fixture.Parts[0].Disposals);
            Check(!fixture.Service.IsRecording, "破棄後も録画中");
            await ExpectSessionFailureAsync<ObjectDisposedException>(() => fixture.Service.StartAsync(SessionSelection(), new AppSettings()));
        })));

        Run("録画連結: 映像copy・無音補完・AAC再符号化を指定", () =>
        {
            var timing = new VideoTiming(0, 2, 320, 240, 60);
            var parts = new RecordingMediaPart[] { new("C:\\test 空白\\silent.mp4", timing, false), new("C:\\test's\\tone.mp4", timing, true) };
            string[] args = RecordingMedia.ConcatArguments("list.ffconcat", parts, "out.mp4");
            Equal("copy", args[Array.IndexOf(args, "-c:v") + 1]);
            Equal("aac", args[Array.IndexOf(args, "-c:a") + 1]);
            string filter = args[Array.IndexOf(args, "-filter_complex") + 1];
            Check(filter.Contains("anullsrc") && filter.Contains("concat=n=2:v=0:a=1"), "音声無し区間を補完していない");
            Check(args.Contains(parts[1].Path) && !filter.Contains(parts[1].Path), "ファイルパスがフィルターへ混入している");
            var silentArgs = RecordingMedia.ConcatArguments("list.ffconcat", parts.Select(part => part with { HasAudio = false }).ToArray(), "out.mp4");
            Check(silentArgs.Contains("-an") && !silentArgs.Contains("-filter_complex"), "全区間音声OFFで音声を作っている");
            string list = RecordingMedia.ConcatList(parts);
            Check(list.StartsWith("ffconcat version 1.0\n", StringComparison.Ordinal) && list.Contains("test'\\''s"), "区間一覧の引用符が不正");
            Equal(2, list.Split("duration 2", StringSplitOptions.None).Length - 1);
        });
    }

    private static CaptureSelection SessionSelection() => new(new Rectangle(-100, 20, 320, 240), null, OutputDestination.File);

    private static async Task ExpectSessionFailureAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(typeof(T).Name + " が発生していません。");
    }

    private sealed class RecordingSessionFixture : IAsyncDisposable
    {
        internal RecordingService Service { get; }
        internal List<FakeRecordingSegment> Parts { get; } = [];
        internal Action<FakeRecordingSegment>? Configure;
        internal Exception? ConcatFailure;
        internal string? SessionDirectory;
        internal int ConcatCalls;
        internal string[] Combined = [];

        internal RecordingSessionFixture(string directory)
        {
            Service = new RecordingService(directory, sessionDirectory =>
            {
                SessionDirectory = sessionDirectory;
                var part = new FakeRecordingSegment(Path.Combine(sessionDirectory, $"segment-{Parts.Count}.mp4"));
                Configure?.Invoke(part);
                Parts.Add(part);
                return part;
            }, (parts, output, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConcatCalls++;
                Combined = parts.ToArray();
                if (ConcatFailure is { } failure) throw failure;
                return Task.FromResult(output);
            });
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed class FakeRecordingSegment(string path) : IRecordingSegment
    {
        public event Action<string>? Failed;
        internal string Path { get; } = path;
        internal string WorkPath => Path + ".pending";
        internal AppSettings? Settings;
        internal int Starts, Stops, Disposals;
        internal Exception? StartFailure, StopFailure, AudioFailure;
        internal Task? StopWait, AudioWait;
        internal List<(bool Loopback, bool Enabled)> AudioChanges { get; } = [];
        internal Action<string>? SnapshotFailure() => Failed;

        public Task StartAsync(CaptureSelection selection, AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            Settings = settings;
            File.WriteAllText(WorkPath, "synthetic recording interval");
            if (StartFailure is { } failure) throw failure;
            return Task.CompletedTask;
        }
        public async Task<string> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            if (StopWait is { } wait) await wait.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (StopFailure is { } failure) throw failure;
            await File.WriteAllTextAsync(Path, "synthetic completed interval", cancellationToken);
            return Path;
        }
        public async Task SetAudioEnabledAsync(bool loopback, bool enabled, CancellationToken cancellationToken = default)
        {
            if (AudioWait is { } wait) await wait.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (AudioFailure is { } failure) throw failure;
            AudioChanges.Add((loopback, enabled));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
