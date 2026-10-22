using System.Security.Principal;
using KotorinToys;

namespace SSCapture.Maintenance;

internal sealed record FileChange(string Path, string? BeforeHash, string? AfterHash);
internal sealed record OperationJournal(string Id, string Owner, string Root, string Kind, string Phase,
    string OldVersion, string NewVersion, FileChange[] Files, string? ReservationBefore = null,
    RegistrationBackup? RegistrationBefore = null, PayloadFile[]? InstallerBefore = null, PayloadFile[]? InstallerAfter = null);

internal sealed class OperationStore
{
    public string DirectoryPath { get; }
    public string JournalPath => SafeFiles.Resolve(DirectoryPath, "operation.json");
    public string Owner => WindowsIdentity.GetCurrent().User!.Value;
    public OperationStore(string root, string? stateDirectory = null)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        DirectoryPath = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SSCaptureMaintenance", key);
        SafeFiles.PrivateDirectory(DirectoryPath);
    }
    public IDisposable Lease() => new FileStream(SafeFiles.Resolve(DirectoryPath, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public OperationJournal? Load(string root)
    {
        if (!File.Exists(JournalPath)) return null;
        var value = SafeFiles.Read<OperationJournal>(JournalPath);
        if (value.Owner != Owner || !string.Equals(value.Root, root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(value.Id, "N", out _) || value.Kind is not ("update" or "remove") ||
            value.Phase is not ("prepared" or "reservations-removed" or "completed" or "recovered") || value.Files is null)
            throw new IOException("操作記録の利用者・対象が一致しません。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in value.Files)
            if (!PayloadManifest.OwnedPath(file.Path) || !seen.Add(file.Path.Replace('\\', '/')) ||
                file.BeforeHash is null && file.AfterHash is null ||
                new[] { file.BeforeHash, file.AfterHash }.Any(h => h != null && !System.Text.RegularExpressions.Regex.IsMatch(h, "^[a-f0-9]{64}$")))
                throw new IOException("操作記録の対象が不正です。");
        foreach (var file in (value.InstallerBefore ?? []).Concat(value.InstallerAfter ?? []))
            if (!InstallerFiles.Names.Contains(file.Path) || file.Length < 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$"))
                throw new IOException("削除補助の退避記録が不正です。");
        return value;
    }
    public void Save(OperationJournal value) => SafeFiles.Write(JournalPath, value);
    public string Backup(OperationJournal value, string relative) => SafeFiles.Resolve(DirectoryPath, value.Id + "/files/" + relative);
}

internal static class InstallerFiles
{
    public static readonly string[] Names = ["unins000.exe", "unins000.dat", "unins000.msg"];
    public static PayloadFile[] Capture(string root) => Names.Select(name => (name, path: SafeFiles.Resolve(root, name)))
        .Where(p => File.Exists(p.path)).Select(p => new PayloadFile(p.name, new FileInfo(p.path).Length, SafeFiles.Hash(p.path))).ToArray();
}

internal sealed class UpdateTransaction(TargetValidator validator, Func<string, Task> stop, OperationStore store)
{
    public async Task Prepare(string root, string payload)
    {
        var pending = store.Load(root);
        if (pending is { Phase: not ("completed" or "recovered") }) throw new IOException("未完了の操作があります。先に復旧してください。");
        var incoming = PayloadManifest.Read(SafeFiles.Resolve(payload, "payload-manifest.json"));
        incoming.Verify(payload);
        var target = validator.Validate(root, true, true);
        if (!target.Fresh)
        {
            if (Version.Parse(incoming.Version) < Version.Parse(target.Version)) throw new IOException("旧版への上書きは許可しません。");
            await stop(root);
            target = validator.Validate(root, true, false);
        }
        var before = target.Manifest?.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(SafeFiles.Resolve(root, "payload-manifest.json"))) before["payload-manifest.json"] = SafeFiles.Hash(SafeFiles.Resolve(root, "payload-manifest.json"));
        var after = incoming.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        after["payload-manifest.json"] = SafeFiles.Hash(SafeFiles.Resolve(payload, "payload-manifest.json"));
        var changes = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Select(path =>
            new FileChange(path, before.GetValueOrDefault(path), after.GetValueOrDefault(path))).ToArray();
        foreach (var entry in changes)
        {
            var file = SafeFiles.Resolve(root, entry.Path);
            if (File.Exists(file) && (entry.BeforeHash is null || SafeFiles.Hash(file) != entry.BeforeHash))
                throw new IOException("配布所有物でないファイルを上書きしません: " + entry.Path);
        }
        var journal = new OperationJournal(Guid.NewGuid().ToString("N"), store.Owner, root, "update", "prepared", target.Version, incoming.Version, changes,
            RegistrationBefore: validator.UsesSystemRegistration ? RegistrationBackup.Capture() : null, InstallerBefore: InstallerFiles.Capture(root));
        foreach (var entry in changes.Where(c => c.BeforeHash != null))
        {
            var backup = store.Backup(journal, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(SafeFiles.Resolve(root, entry.Path), backup, false);
            if (SafeFiles.Hash(backup) != entry.BeforeHash) throw new IOException("更新前ファイルを退避できません。");
        }
        foreach (var entry in journal.InstallerBefore!)
        {
            var backup = store.Backup(journal, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(SafeFiles.Resolve(root, entry.Path), backup, false);
            if (SafeFiles.Hash(backup) != entry.Sha256) throw new IOException("削除補助を退避できません。");
        }
        // 記録を公開するまではsetupが書込を開始できない。
        store.Save(journal);
    }
    public void ObserveInstalled(string root)
    {
        var journal = Required(root);
        if (journal.Phase != "prepared") throw new IOException("配置直後の操作状態ではありません。");
        var manifest = PayloadManifest.Read(SafeFiles.Resolve(root, "payload-manifest.json"));
        if (manifest.Version != journal.NewVersion) throw new IOException("配置後の目録版が一致しません。");
        manifest.Verify(root);
        // ssPostInstallで例外が出てもInnoは登録を戻さないため、配置直後の所有状態を先に記録する。
        if (journal.InstallerAfter != null)
        {
            if (!journal.InstallerAfter.SequenceEqual(InstallerFiles.Capture(root)))
                throw new IOException("配置確認後に削除補助が変わっています。");
            return;
        }
        journal = journal with { InstallerAfter = InstallerFiles.Capture(root) };
        store.Save(journal);
    }
    public void Commit(string root)
    {
        ObserveInstalled(root);
        var journal = Required(root);
        var target = validator.Validate(root, false, false);
        if (target.Version != journal.NewVersion) throw new IOException("更新後の版が一致しません。");
        foreach (var entry in journal.Files.Where(c => c.AfterHash is null))
        {
            var file = SafeFiles.Resolve(root, entry.Path);
            if (!File.Exists(file)) continue;
            if (SafeFiles.Hash(file) != entry.BeforeHash) throw new IOException("旧所有ファイルが外部変更されています。");
            File.Delete(file);
        }
        store.Save(journal with { Phase = "completed" });
    }
    public void Recover(string root)
    {
        var journal = Required(root);
        if (journal.Phase is "completed" or "recovered") return;
        journal.RegistrationBefore?.ValidateCurrent(root, journal.OldVersion, journal.NewVersion);
        // 全件を先に照合し、外部の変更があるときは一部だけ復元しない。
        foreach (var entry in journal.Files)
        {
            var path = SafeFiles.Resolve(root, entry.Path);
            if (File.Exists(path) && SafeFiles.Hash(path) is var hash && hash != entry.BeforeHash && hash != entry.AfterHash)
                throw new IOException("更新後に外部変更されたため復旧を中止しました: " + entry.Path);
            if (entry.BeforeHash != null && SafeFiles.Hash(store.Backup(journal, entry.Path)) != entry.BeforeHash)
                throw new IOException("退避ファイルが一致しません。");
        }
        foreach (var name in InstallerFiles.Names)
        {
            var before = journal.InstallerBefore?.SingleOrDefault(f => f.Path == name);
            var after = journal.InstallerAfter?.SingleOrDefault(f => f.Path == name);
            var path = SafeFiles.Resolve(root, name);
            if (File.Exists(path) && SafeFiles.Hash(path) is var hash && hash != before?.Sha256 && hash != after?.Sha256)
                throw new IOException("削除補助の外部変更を検出しました: " + name);
            if (before != null && SafeFiles.Hash(store.Backup(journal, name)) != before.Sha256)
                throw new IOException("削除補助の退避が一致しません。");
        }
        foreach (var entry in journal.Files)
        {
            var path = SafeFiles.Resolve(root, entry.Path);
            if (entry.BeforeHash != null) SafeFiles.RestoreFile(store.Backup(journal, entry.Path), path);
            else if (File.Exists(path)) File.Delete(path);
        }
        foreach (var name in InstallerFiles.Names)
        {
            var before = journal.InstallerBefore?.SingleOrDefault(f => f.Path == name);
            var path = SafeFiles.Resolve(root, name);
            if (before != null) SafeFiles.RestoreFile(store.Backup(journal, name), path);
            else if (File.Exists(path)) File.Delete(path);
        }
        journal.RegistrationBefore?.Restore(root, journal.OldVersion, journal.NewVersion);
        // 新規導入取消後は、この配布で作った空の下位フォルダーだけを片付ける。
        // 利用者が追加したファイル・フォルダーを再帰削除しない。
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Files)
            for (var directory = Path.GetDirectoryName(SafeFiles.Resolve(root, entry.Path));
                 directory != null && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase);
                 directory = Path.GetDirectoryName(directory))
                directories.Add(directory);
        foreach (var directory in directories.OrderByDescending(p => p.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        if (journal.OldVersion.Length != 0)
        {
            var target = validator.Validate(root, true, false);
            if (target.Version != journal.OldVersion) throw new IOException("ファイル復旧済みですが正式登録の復旧が未確認です。正規setupで再確認してください。");
        }
        else if (validator.CurrentRegistration() is not null)
            throw new IOException("新規導入の正式登録が残っています。setupの復旧結果を確認してください。");
        store.Save(journal with { Phase = "recovered" });
    }
    OperationJournal Required(string root)
    {
        var value = store.Load(root);
        if (value is null || value.Kind != "update") throw new IOException("更新操作記録がありません。");
        return value;
    }
}
