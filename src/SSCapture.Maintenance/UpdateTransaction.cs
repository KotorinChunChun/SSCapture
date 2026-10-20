using System.Security.Principal;
using KotorinToys;

namespace SSCapture.Maintenance;

internal sealed record FileChange(string Path, string? BeforeHash, string? AfterHash);
internal sealed record OperationJournal(string Id, string Owner, string Root, string Kind, string Phase,
    string OldVersion, string NewVersion, FileChange[] Files, string? ReservationBefore = null);

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
        return value;
    }
    public void Save(OperationJournal value) => SafeFiles.Write(JournalPath, value);
    public string Backup(OperationJournal value, string relative) => SafeFiles.Resolve(DirectoryPath, value.Id + "/files/" + relative);
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
        var journal = new OperationJournal(Guid.NewGuid().ToString("N"), store.Owner, root, "update", "prepared", target.Version, incoming.Version, changes);
        foreach (var entry in changes.Where(c => c.BeforeHash != null))
        {
            var backup = store.Backup(journal, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(SafeFiles.Resolve(root, entry.Path), backup, false);
            if (SafeFiles.Hash(backup) != entry.BeforeHash) throw new IOException("更新前ファイルを退避できません。");
        }
        // 記録を公開するまではsetupが書込を開始できない。
        store.Save(journal);
    }
    public void Commit(string root)
    {
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
        // 全件を先に照合し、外部の変更があるときは一部だけ復元しない。
        foreach (var entry in journal.Files)
        {
            var path = SafeFiles.Resolve(root, entry.Path);
            if (File.Exists(path) && SafeFiles.Hash(path) is var hash && hash != entry.BeforeHash && hash != entry.AfterHash)
                throw new IOException("更新後に外部変更されたため復旧を中止しました: " + entry.Path);
            if (entry.BeforeHash != null && SafeFiles.Hash(store.Backup(journal, entry.Path)) != entry.BeforeHash)
                throw new IOException("退避ファイルが一致しません。");
        }
        foreach (var entry in journal.Files)
        {
            var path = SafeFiles.Resolve(root, entry.Path);
            if (entry.BeforeHash != null) SafeFiles.RestoreFile(store.Backup(journal, entry.Path), path);
            else if (File.Exists(path)) File.Delete(path);
        }
        // 登録・uninsのロールバックはInnoが所有する。整合しなければ完了にしない。
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
