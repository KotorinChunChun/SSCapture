using System.Diagnostics;
using System.Text;

namespace SSCapture.Recording;

/// <summary>Owns exactly one child process and drains both pipes throughout its lifetime.</summary>
internal sealed class MediaProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stdoutTask;
    private readonly Task stderrTask;
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    internal Task Exit { get; }
    internal TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ExitCode => process.ExitCode;
    internal string Output { get { lock (stdout) return stdout.ToString(); } }
    internal string Error { get { lock (stderr) return stderr.ToString(); } }

    internal MediaProcess(string executable, IEnumerable<string> arguments, Action<string>? onOutput = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new InvalidOperationException("FFmpeg を開始できません。"); }
        catch { process.Dispose(); throw; }
        stdoutTask = DrainAsync(process.StandardOutput, stdout, line =>
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(6).Trim(), out var count) && count > 0) FirstFrame.TrySetResult();
            onOutput?.Invoke(line);
        });
        stderrTask = DrainAsync(process.StandardError, stderr);
        Exit = ObserveExitAsync();
    }

    private async Task ObserveExitAsync()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
    }
    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, Action<string>? onLine = null)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (buffer)
            {
                buffer.AppendLine(line);
                if (buffer.Length > 65_536) buffer.Remove(0, buffer.Length - 65_536);
            }
            onLine?.Invoke(line);
        }
    }

    internal async Task StopGracefullyAsync(TimeSpan timeout)
    {
        if (!process.HasExited)
        {
            try { await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false); await process.StandardInput.FlushAsync().ConfigureAwait(false); }
            catch (IOException) when (process.HasExited) { }
            try { await Exit.WaitAsync(timeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                await KillAsync().ConfigureAwait(false);
                throw new TimeoutException("FFmpeg が停止期限を超えたため終了しました。作業ファイルは保持しています。");
            }
        }
        await Exit.ConfigureAwait(false);
        EnsureSuccess();
    }
    internal void EnsureSuccess()
    {
        if (ExitCode != 0) throw new InvalidOperationException($"FFmpeg/ffprobe が終了コード {ExitCode} で失敗しました。\n{Error}");
    }
    internal async Task KillAsync()
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await Exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        try { await KillAsync().ConfigureAwait(false); }
        finally { process.Dispose(); }
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, Action<string>? onOutput = null, TimeSpan? timeout = null)
    {
        await using var child = new MediaProcess(executable, arguments, onOutput);
        await child.Exit.WaitAsync(timeout ?? TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        child.EnsureSuccess();
        return child.Output;
    }
}
