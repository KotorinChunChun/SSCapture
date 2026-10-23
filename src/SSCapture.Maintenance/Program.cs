using System.Text.Json;
using KotorinToys;
using SSCapture.Maintenance;

string? diagnosticRoot = null;
string diagnosticOperation = "";
try
{
    if (args.Length % 2 != 0) throw new IOException("引数は名前と値の組で指定してください。");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 0; index < args.Length; index += 2)
        if (!new[] { "--product", "--root", "--operation", "--payload" }.Contains(args[index]) || !values.TryAdd(args[index], args[index + 1]))
            throw new IOException("未知または重複した引数です。");
    if (values.GetValueOrDefault("--product") != "SSCapture") throw new IOException("この補助はSSCapture専用です。");
    var root = SafeFiles.Root(values.GetValueOrDefault("--root") ?? "");
    var operation = values.GetValueOrDefault("--operation") ?? "";
    diagnosticRoot = root;
    diagnosticOperation = operation;
    var validator = new TargetValidator();
    if (operation is "startup-status" or "startup-enable" or "startup-disable")
    {
        var tool = TargetValidator.Definition with { Capabilities = ["status", "show", "stop", "silent-start"] };
        bool InstalledHere()
        {
            validator.Validate(root, false, false);
            var installed = new InstallationDetector().Detect(tool);
            return installed.State == "導入済み" && string.Equals(installed.Directory, root, StringComparison.OrdinalIgnoreCase);
        }
        var startup = new StandaloneStartup(new(HostSettingsStore.DefaultDirectory), new TaskSchedulerGateway(), tool,
            Path.Combine(root, "SSCapture.exe"), InstalledHere);
        if (operation == "startup-status")
            Console.WriteLine(JsonSerializer.Serialize(startup.State(), SafeFiles.Json));
        else
        {
            var changed = startup.SetEnabled(operation == "startup-enable");
            Console.WriteLine(JsonSerializer.Serialize(changed, SafeFiles.Json));
            return changed.Success ? 0 : 1;
        }
        return 0;
    }
    if (operation is "verify" or "status" or "show" or "start" or "stop")
    {
        validator.Validate(root, false, false);
        var detected = new InstallationDetector().Detect(TargetValidator.Definition);
        if (detected.State != "導入済み" || !string.Equals(detected.Directory, root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("本体の導入検出で対象を確認できません。");
        var manager = new ToolManager(root);
        object result = operation switch
        {
            "verify" => detected,
            "start" => await manager.Start(TargetValidator.Definition, showWindow: false),
            "show" => await manager.Start(TargetValidator.Definition, showWindow: true),
            "stop" => await manager.Stop(TargetValidator.Definition),
            _ => await manager.Status(TargetValidator.Definition)
        };
        Console.WriteLine(JsonSerializer.Serialize(result, SafeFiles.Json));
        if (result is ToolState state && state.State != (operation == "stop" ? "停止中" : operation == "status" ? state.State : "実行中")) return 1;
        if (result is ToolState { State: "確認できない" or "起動処理中" or "停止処理中" }) return 1;
        return 0;
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    async Task Stop(string target)
    {
        // 呼出元は登録・目録・旧版を既に照合済み。欠落tool.jsonを回避するのはこの限定呼出だけ。
        var managed = TargetValidator.Definition with { AppId = "" };
        var result = await new ToolManager(target).Stop(managed, timeout.Token);
        if (result.State != "停止中") throw new IOException("正常終了を確認できません: " + result.State + " / " + result.Detail);
    }
    if (!new[] { "update", "observe-update", "commit-update", "recover-update", "remove", "commit-remove", "recover-remove" }.Contains(operation))
        throw new IOException("未対応の操作です。");
    var store = new OperationStore(root);
    using var lease = store.Lease();
    var update = new UpdateTransaction(validator, Stop, store);
    var remove = new RemovalTransaction(validator, Stop, store, new HostSettingsStore(HostSettingsStore.DefaultDirectory), new TaskSchedulerGateway());
    switch (operation)
    {
        case "update":
            var payload = SafeFiles.Root(values.GetValueOrDefault("--payload") ?? "");
            await update.Prepare(root, payload);
            break;
        case "commit-update": update.Commit(root); break;
        case "observe-update": update.ObserveInstalled(root); break;
        case "recover-update":
            if (store.Load(root) is { Kind: "update" }) update.Recover(root);
            break;
        case "remove":
            await remove.Prepare(root);
            break;
        case "commit-remove": remove.Commit(root); break;
        case "recover-remove":
            if (store.Load(root) is { Kind: "remove" }) remove.Recover(root);
            break;
    }
    Console.WriteLine("対象照合・正常停止・所有物の処理を完了しました。利用者データは保持します。");
    return 0;
}
catch (Exception ex)
{
    if (diagnosticRoot != null)
    {
        try
        {
            var store = new OperationStore(diagnosticRoot);
            SafeFiles.Write(SafeFiles.Resolve(store.DirectoryPath, diagnosticOperation + "-error.json"),
                new { root = diagnosticRoot, operation = diagnosticOperation, utc = DateTimeOffset.UtcNow, message = ex.Message });
        }
        catch (Exception) { /* 診断記録失敗でも操作の失敗を成功へ変えない。 */ }
    }
    Console.Error.WriteLine("SSCapture管理処理を中止しました: " + ex.Message);
    return 1;
}
