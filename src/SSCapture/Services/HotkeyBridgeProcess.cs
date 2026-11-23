using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SSCapture.Services;

internal static class HotkeyBridgeProcess
{
    internal static bool IsElevated
    {
        get
        {
            using var user = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static bool IsInstalled()
    {
        string path = HotkeyBridgeProtocol.InstalledPath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        for (string? item = path; item is not null; item = Path.GetDirectoryName(item))
            if (File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint)) return false;
        return true;
    }

    internal static int? FindPeer(SafePipeHandle pipe, bool client, bool elevated, string expectedExecutable, bool compareFullPath)
    {
        bool found = client ? GetNamedPipeClientProcessId(pipe, out int pid) : GetNamedPipeServerProcessId(pipe, out pid);
        if (!found || !ProcessIdToSessionId(pid, out int session)) return null;
        using var current = Process.GetCurrentProcess();
        if (session == 0 || session != current.SessionId) return null;
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return null;
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            using var ownIdentity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user || ownIdentity.User is not { } ownUser || !user.Equals(ownUser)
                || !GetTokenInformation(token, 20, out int value, sizeof(int), out _) || (value != 0) != elevated)
                return null;
            var buffer = new StringBuilder(32768);
            int length = buffer.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref length)) return null;
            string path = buffer.ToString();
            return string.Equals(compareFullPath ? path : Path.GetFileName(path), expectedExecutable, StringComparison.OrdinalIgnoreCase)
                ? pid : null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int pid, out int sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int bytesReturned);
}
