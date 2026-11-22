using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SSCapture.Tests;

internal sealed class BrowserFixtureJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int JobAccountingInformation = 1;
    private const int JobExtendedLimitInformation = 9;
    private static readonly nuint JobListAttribute = 0x0002000D;
    private readonly SafeFileHandle job;

    internal BrowserFixtureJob()
    {
        job = CreateJobObject(0, null);
        var limits = new ExtendedLimitInformation { Basic = new() { LimitFlags = KillOnJobClose } };
        if (!job.IsInvalid && SetInformationJobObject(job, JobExtendedLimitInformation, in limits,
            (uint)Marshal.SizeOf<ExtendedLimitInformation>())) return;
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        job.Dispose();
        throw error;
    }

    internal void Start(string executable, IReadOnlyList<string> args, string directory)
    {
        executable = Path.GetFullPath(executable);
        // All callers use file paths, fixed switches or a percent-encoded file URL.
        string Quote(string value) => value.Contains('"') || value.EndsWith('\\')
            ? throw new ArgumentException("検証プロセスの引数を正しく引用できません。")
            : "\"" + value + "\"";
        var command = new StringBuilder(string.Join(" ", args.Prepend(executable).Select(Quote)));
        nuint bytes = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
        if (bytes == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint attributes = Marshal.AllocHGlobal(checked((int)bytes));
        nint handle = 0;
        bool initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes);
            if (!initialized) throw new Win32Exception(Marshal.GetLastWin32Error());
            handle = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(handle, job.DangerousGetHandle());
            // Attach at creation so the browser cannot start a child before it belongs to the job.
            if (!UpdateProcThreadAttribute(attributes, 0, JobListAttribute, handle, (nuint)nint.Size, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx { Info = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            if (!CreateProcess(executable, command, 0, 0, false, ExtendedStartupInfoPresent | CreateNoWindow,
                0, directory, in startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var processHandle = new SafeFileHandle(process.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(process.Thread, ownsHandle: true);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handle);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(job);
        }
    }

    internal bool Contains(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return IsProcessInJob(process.SafeHandle, job, out bool belongs) && belongs;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    internal void WaitUntilEmpty()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (!QueryInformationJobObject(job, JobAccountingInformation, out var info, (uint)Marshal.SizeOf<AccountingInformation>(), 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.ActiveProcesses == 0) return;
            Thread.Sleep(25);
        }
        throw new TimeoutException("ブラウザー検証のプロセスが残っています。");
    }

    public void Dispose()
    {
        if (job.IsClosed) return;
        try
        {
            if (!TerminateJobObject(job, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            WaitUntilEmpty();
        }
        finally { job.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTimeLimit, JobTimeLimit;
        public uint LimitFlags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInformation
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public nint ReservedPointer, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, in ExtendedLimitInformation info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out AccountingInformation info, uint size, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint key, nint value, nuint bytes, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string executable, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory,
        in StartupInfoEx startup, out ProcessInformation process);
}
