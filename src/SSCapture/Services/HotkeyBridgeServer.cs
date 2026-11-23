using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SSCapture.Services;

internal sealed class HotkeyBridgeServer : IDisposable
{
    private readonly KeyboardHook hook;
    private readonly HotkeyAssignment[] assignments;
    private readonly HotkeyBinding[] bindings;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private CancellationTokenSource? lease;
    private int activePid;
    internal int? ActivePid => Volatile.Read(ref activePid) is int pid && pid != 0 ? pid : null;

    private HotkeyBridgeServer(KeyboardHook hook, HotkeyAssignment[] assignments)
    {
        this.hook = hook;
        this.assignments = assignments;
        bindings = assignments.Select(assignment => assignment.Binding).ToArray();
        _ = Task.Run(ServeAsync);
    }

    internal static HotkeyBridgeServer? TryStart(KeyboardHook hook, HotkeyAssignment[] assignments)
    {
        try
        {
            if (!HotkeyBridgeProcess.IsInstalled() || HotkeyBridgeProcess.IsElevated) return null;
            var allowed = assignments.Where(item => HotkeyBridgeProtocol.Supports(item.Binding)).ToArray();
            return allowed.Length == 0 ? null : new(hook, allowed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private async Task ServeAsync()
    {
        CancellationToken stopping = lifetime.Token;
        try
        {
            string name = HotkeyBridgeProtocol.CurrentPipeName;
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("撮影キー補助の利用者を確認できません。");
            var permissions = new PipeSecurity();
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.SetOwner(user);
            permissions.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            permissions.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 128, 128, permissions);
                    await pipe.WaitForConnectionAsync(stopping);
                    if (hook.Enabled && HotkeyBridgeProcess.FindPeer(pipe.SafePipeHandle, client: true, elevated: true,
                        HotkeyBridgeProtocol.InstalledPath, compareFullPath: true) is int pid)
                        await ServeLeaseAsync(pipe, pid, stopping);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException
                    or System.Security.SecurityException or System.ComponentModel.Win32Exception) { }
                await Task.Delay(100, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ServeLeaseAsync(Stream pipe, int pid, CancellationToken stopping)
    {
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lock (gate)
        {
            if (!hook.Enabled) return;
            lease = connected;
        }
        try
        {
            connected.CancelAfter(TimeSpan.FromSeconds(12));
            await HotkeyBridgeProtocol.SendBindingsAsync(pipe, bindings, connected.Token);
            if (await HotkeyBridgeProtocol.ReadByteAsync(pipe, connected.Token) != HotkeyBridgeProtocol.Ready) return;
            hook.SetBridgeBindings(bindings.ToHashSet());
            await pipe.WriteAsync(new byte[] { HotkeyBridgeProtocol.Activate }, connected.Token);
            Volatile.Write(ref activePid, pid);
            connected.CancelAfter(Timeout.InfiniteTimeSpan);
            while (!connected.IsCancellationRequested)
            {
                int index = await HotkeyBridgeProtocol.ReadByteAsync(pipe, connected.Token);
                if (index >= assignments.Length) return;
                if (!connected.IsCancellationRequested) hook.DispatchBridgeBinding(assignments[index]);
            }
        }
        finally
        {
            Volatile.Write(ref activePid, 0);
            hook.SetBridgeBindings(null);
            lock (gate) { lease = null; }
        }
    }

    internal void CancelLease()
    {
        lock (gate) lease?.Cancel();
        Volatile.Write(ref activePid, 0);
    }
    public void Dispose() => lifetime.Cancel();
}
