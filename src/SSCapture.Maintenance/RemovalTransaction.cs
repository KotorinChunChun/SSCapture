using System.Text.Json;
using KotorinToys;

namespace SSCapture.Maintenance;

internal sealed class RemovalTransaction(TargetValidator validator, Func<string, Task> stop, OperationStore operations,
    HostSettingsStore settings, IStartupGateway gateway)
{
    readonly RemovalReservations reservations = new(settings, gateway, definitions: [TargetValidator.Definition]);
    static string Serialize(HostSettings value) => JsonSerializer.Serialize(value, ToolCatalog.Json);
    public async Task Prepare(string root)
    {
        if (operations.Load(root) is { Phase: not ("completed" or "recovered") }) throw new IOException("未完了の操作があります。先に復旧してください。");
        validator.Validate(root, false, false);
        await stop(root);
        var target = validator.Validate(root, false, false);
        var before = Serialize(settings.Load());
        var journal = new OperationJournal(Guid.NewGuid().ToString("N"), operations.Owner, root, "remove", "prepared",
            target.Version, "", [], before);
        operations.Save(journal);
        try
        {
            var result = reservations.Remove("ss-capture");
            if (!result.Success) throw new IOException(result.Detail);
            CheckRemovalJournal(journal, completed: true);
            operations.Save(journal with { Phase = "reservations-removed" });
        }
        catch
        {
            // 準備中の失敗は呼出元が「準備成功」を記録する前に発生する。
            // この実行の記録から復旧し、復旧不可なら未完了記録を残す。
            Recover(root);
            throw;
        }
    }
    public void Recover(string root)
    {
        var journal = Required(root);
        if (journal.Phase is "completed" or "recovered") return;
        var pending = File.Exists(Path.Combine(settings.DirectoryPath, "removal-operation.json"));
        if (!pending && Serialize(settings.Load()) == journal.ReservationBefore)
        { operations.Save(journal with { Phase = "recovered" }); return; }
        CheckRemovalJournal(journal, completed: !pending);
        var result = reservations.Recover(completed: !pending, expectedId: "ss-capture");
        if (!result.Success) throw new IOException(result.Detail);
        operations.Save(journal with { Phase = "recovered" });
    }
    public void Commit(string root)
    {
        var journal = Required(root);
        if (journal.Phase != "reservations-removed") throw new IOException("予約整理の完了を確認できません。");
        operations.Save(journal with { Phase = "completed" });
    }
    void CheckRemovalJournal(OperationJournal operation, bool completed)
    {
        var path = Path.Combine(settings.DirectoryPath, completed ? "last-removal-operation.json" : "removal-operation.json");
        var journal = SafeFiles.Read<RemovalJournal>(path);
        if (journal.TargetId != "ss-capture" || Serialize(journal.Before) != operation.ReservationBefore)
            throw new IOException("別の操作記録へ変わっています。予約を復元しません。");
    }
    OperationJournal Required(string root)
    {
        var journal = operations.Load(root);
        if (journal is null || journal.Kind != "remove") throw new IOException("削除操作記録がありません。");
        return journal;
    }
}
