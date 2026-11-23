using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using SSCapture;
using SSCapture.Services;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!args.SequenceEqual(["--startup"]) || !HotkeyBridgeProcess.IsElevated
            || !HotkeyBridgeProcess.IsInstalled()
            || !string.Equals(Environment.ProcessPath, HotkeyBridgeProtocol.InstalledPath, StringComparison.OrdinalIgnoreCase))
            return 2;

        string name = HotkeyBridgeProtocol.CurrentPipeName;
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(2000);
                if (HotkeyBridgeProcess.FindPeer(pipe.SafePipeHandle, client: false, elevated: false,
                    expectedExecutable: "SSCapture.exe", compareFullPath: false) is not null)
                    await RunAsync(pipe);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.Delay(500);
        }
    }

    private static async Task RunAsync(Stream pipe)
    {
        using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bindings = await HotkeyBridgeProtocol.ReadBindingsAsync(pipe, handshake.Token);
        var assignments = bindings.Select((binding, index) => new HotkeyAssignment(binding, CaptureIntent.Screenshot, DisplayNumber: index + 1)).ToArray();
        var pending = Channel.CreateBounded<byte>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        using var hook = new KeyboardHook(assignments, decision => pending.Writer.TryWrite((byte)(decision.DisplayNumber - 1)), initiallyEnabled: false);
        await pipe.WriteAsync(new byte[] { HotkeyBridgeProtocol.Ready }, handshake.Token);
        if (await HotkeyBridgeProtocol.ReadByteAsync(pipe, handshake.Token) != HotkeyBridgeProtocol.Activate) return;
        using var connected = new CancellationTokenSource();
        hook.Enabled = true;
        Task closed = pipe.ReadAsync(new byte[1], connected.Token).AsTask();
        Task send = SendAsync();
        try { await await Task.WhenAny(closed, send); }
        finally
        {
            hook.Enabled = false;
            connected.Cancel();
            try { await Task.WhenAll(closed, send); }
            catch (OperationCanceledException) when (connected.IsCancellationRequested) { }
        }

        async Task SendAsync()
        {
            await foreach (byte index in pending.Reader.ReadAllAsync(connected.Token))
                await pipe.WriteAsync(new byte[] { index }, connected.Token);
        }
    }
}
