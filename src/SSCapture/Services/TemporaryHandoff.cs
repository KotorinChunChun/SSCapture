using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SSCapture.Services;

// 命名の衝突は公開先のファイルで判定し、回収はパスと生成時のファイルIDで判定する。
internal sealed class TemporaryHandoff : IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(1);
    private const string LeaseDirectory = ".handoff-leases";
    private readonly Lease record;
    private readonly object gate = new();
    private long expiresAt;
    private bool reclaimScheduled;
    private FileStream? lease;
    internal string Path { get; }
    private bool IsAvailable => FileIdentity.Read(Path) == record.Identity;
    internal void CompleteMove() => FileIdentity.DeleteIfMatches(Path, record.Identity);

    private TemporaryHandoff(string path, Lease record, FileStream lease)
    {
        Path = path;
        this.record = record;
        this.lease = lease;
    }

    internal static string DirectoryFor(string workRoot) => System.IO.Path.Combine(workRoot, "notification-images");

    internal static TemporaryHandoff Publish(string directory, Func<Action<string, string>, string> write)
    {
        directory = System.IO.Path.GetFullPath(directory);
        string leases = System.IO.Path.Combine(directory, LeaseDirectory);
        EnsureDirectory(directory); EnsureDirectory(leases);
        var lockFile = new FileStream(System.IO.Path.Combine(leases, Guid.NewGuid().ToString("N") + ".json"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
        Lease? record = null;
        try
        {
            string path = write((staged, destination) =>
            {
                var identity = record?.Identity ?? FileIdentity.Read(staged)
                    ?? throw new IOException("受け渡しファイルを識別できません。");
                record = new(System.IO.Path.GetFileName(destination), System.IO.Path.GetFileName(staged), identity);
                // 完成ファイルを公開する前に所有者を記録し、終了時の回収でも同じIDを使う。
                lockFile.Position = 0;
                lockFile.SetLength(0);
                JsonSerializer.Serialize(lockFile, record);
                lockFile.Flush(true);
            });
            return new(path, record ?? throw new IOException("受け渡しファイルの所有者を記録できませんでした。"), lockFile);
        }
        catch
        {
            CloseLease(lockFile);
            throw;
        }
    }

    internal bool TryRenew()
    {
        lock (gate)
        {
            if (lease is null || !IsAvailable) return false;
            expiresAt = Environment.TickCount64 + (long)Retention.TotalMilliseconds;
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (lease is null) return;
            expiresAt = Environment.TickCount64 + (long)Retention.TotalMilliseconds;
            if (reclaimScheduled) return;
            reclaimScheduled = true;
        }
        _ = ReclaimWhenIdleAsync();
    }

    private async Task ReclaimWhenIdleAsync()
    {
        while (true)
        {
            long remaining;
            FileStream? owner = null;
            lock (gate)
            {
                remaining = expiresAt - Environment.TickCount64;
                if (remaining <= 0) { owner = lease; lease = null; }
            }

            if (owner is not null)
            {
                await ReclaimAfterAsync(Path, record, owner, TimeSpan.Zero);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(remaining)).ConfigureAwait(false);
        }
    }

    internal static void CleanupPreviousSession(string directory, DateTimeOffset? now = null)
    {
        string leases = System.IO.Path.Combine(directory, LeaseDirectory);
        if (!IsDirectory(directory) || !IsDirectory(leases)) return;
        foreach (string marker in System.IO.Directory.EnumerateFiles(leases, "*.json"))
        {
            if (!Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(marker), "N", out _)) continue;
            FileStream? owner = null;
            try
            {
                if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) continue;
                // 別プロセスが利用中なら排他的な所有ファイルを開けない。
                owner = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                if (owner.Length is 0 or > 4096) continue;
                var record = JsonSerializer.Deserialize<Lease>(owner);
                if (record is null) continue;
                FileNamePattern.ValidateFileName(record.FileName);
                string extension = System.IO.Path.GetExtension(record.FileName).ToLowerInvariant();
                if (extension is not (".png" or ".mp4")) continue;
                FileNamePattern.ValidateFileName(record.StagingFileName);
                if (!record.StagingFileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                TimeSpan remaining = Retention - ((now ?? DateTimeOffset.UtcNow) - File.GetLastWriteTimeUtc(marker));
                _ = ReclaimAfterAsync(System.IO.Path.Combine(directory, record.FileName), record, owner,
                    remaining > Retention ? Retention : remaining);
                owner = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            finally { owner?.Dispose(); }
        }
    }

    private static async Task ReclaimAfterAsync(string path, Lease record, FileStream owner, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        // アプリが排他的に開いている場合は記録を残し、次回起動時の回収に委ねる。
        string staged = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, record.StagingFileName);
        bool publishedGone = FileIdentity.DeleteIfMatches(path, record.Identity);
        bool stagedGone = FileIdentity.DeleteIfMatches(staged, record.Identity);
        if (publishedGone && stagedGone) CloseLease(owner);
        else owner.Dispose();
    }

    private static void CloseLease(FileStream owner)
    {
        string marker = owner.Name;
        owner.Dispose();
        try { File.Delete(marker); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsDirectory(string path) => System.IO.Directory.Exists(path)
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static void EnsureDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        if (!IsDirectory(path)) throw new IOException("受け渡し用フォルダを安全に開けません。");
    }

    private sealed record Lease(string FileName, string StagingFileName, FileIdentity Identity);

    private readonly record struct FileIdentity(uint Volume, ulong Index, ulong Created)
    {
        private const uint ReadAttributes = 0x80, Delete = 0x10000;
        private const uint OpenExisting = 3, OpenReparsePoint = 0x00200000;

        internal static FileIdentity? Read(string path)
        {
            using var file = CreateFileW(NativePath(path), ReadAttributes, FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, OpenReparsePoint, 0);
            return Identify(file);
        }

        internal static bool DeleteIfMatches(string path, FileIdentity expected)
        {
            // 同じパスが別ファイルへ置き換わることを防ぎ、確認したハンドルだけを削除する。
            using var file = CreateFileW(NativePath(path), ReadAttributes | Delete, FileShare.ReadWrite, 0, OpenExisting, OpenReparsePoint, 0);
            if (file.IsInvalid) return Marshal.GetLastWin32Error() is 2 or 3;
            if (Identify(file) != expected) return true;
            byte deleteFile = 1;
            return SetFileInformationByHandle(file, 4 /* FileDispositionInfo */, ref deleteFile, 1);
        }

        private static string NativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

        private static FileIdentity? Identify(SafeFileHandle file)
        {
            if (file.IsInvalid || !GetFileInformationByHandle(file, out var info)
                || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
            return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
                ((ulong)info.CreatedHigh << 32) | info.CreatedLow);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal FileAttributes Attributes;
            internal uint CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint Volume, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref byte data, uint size);
    }
}
