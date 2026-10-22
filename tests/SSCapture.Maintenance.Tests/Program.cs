using System.Security.Principal;
using System.Text.Json;
using KotorinToys;
using SSCapture.Maintenance;

var output = Path.GetFullPath(args.FirstOrDefault() ?? throw new ArgumentException("専用fixture出力先を指定してください。"));
Directory.CreateDirectory(output);
int passed = 0, failed = 0;
async Task Run(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void Check(bool value) { if (!value) throw new Exception("期待値不一致"); }
static async Task Rejected(Func<Task> action)
{
    try { await action(); } catch (IOException) { return; }
    throw new Exception("拒否されませんでした");
}
static Task Done(Action action) { action(); return Task.CompletedTask; }
Fixture New() => new(Path.Combine(output, Guid.NewGuid().ToString("N")));

await Run("既知旧版のtool.json欠落だけを認識", () => Done(() =>
{
    var f = New();
    Check(f.Validator.Validate(f.Root, true, false).Legacy);
    Check(!File.Exists(Path.Combine(f.Root, "tool.json")));
}));
await Run("正式登録なしを拒否", async () => { var f = New(); f.Registration = null; await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, true))); });
await Run("既知日本語setupの旧版表示名を限定認識", async () =>
{
    var f = New(); f.Registration = f.Registration! with { Name = "SSCapture バージョン 0.1.2" };
    Check(f.Validator.Validate(f.Root, true, false).Legacy);
    f.Registration = f.Registration with { Name = "SSCapture バージョン 0.1.1" };
    await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false)));
});
await Run("別製品名を拒否", async () => { var f = New(); f.Registration = f.Registration! with { Name = "Other" }; await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false))); });
await Run("登録先違いを拒否", async () => { var f = New(); f.Registration = f.Registration! with { Directory = f.Payload }; await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false))); });
await Run("削除登録違いを拒否", async () => { var f = New(); f.Registration = f.Registration! with { Uninstall = "\"C:\\other\\unins000.exe\"" }; await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false))); });
await Run("既知版以外の欠落を拒否", async () => { var f = New(); f.Registration = f.Registration! with { Version = "0.1.1", Name = "SSCapture" }; await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false))); });
await Run("実EXEの別製品を拒否", async () => { var f = New(); f.Identity = f.Identity with { Product = "Other" }; await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false))); });
await Run("旧配布物の改変を拒否", async () => { var f = New(); File.WriteAllText(Path.Combine(f.Root, "SSCapture.dll"), "changed"); await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false))); });
await Run("存在する不正定義は欠落と扱わない", async () =>
{
    var f = New(); SafeFiles.Write(Path.Combine(f.Root, "tool.json"), new { schemaVersion = 1, id = "other", name = "SSCapture", appId = "KotorinToys.SSCapture", version = "0.1.2", installedExecutable = "SSCapture.exe" });
    await Rejected(() => Done(() => f.Validator.Validate(f.Root, true, false)));
});
await Run("正常停止拒否では退避・更新を開始しない", async () =>
{
    var f = New();
    var transaction = new UpdateTransaction(f.Validator, _ => throw new IOException("作業中"), f.Store);
    await Rejected(() => transaction.Prepare(f.Root, f.Payload));
    Check(f.Store.Load(f.Root) is null);
    Check(File.ReadAllText(Path.Combine(f.Root, "SSCapture.exe")) == "old");
});
await Run("停止後の対象差替を拒否", async () =>
{
    var f = New();
    var transaction = new UpdateTransaction(f.Validator, _ => { f.Registration = null; return Task.CompletedTask; }, f.Store);
    await Rejected(() => transaction.Prepare(f.Root, f.Payload));
    Check(f.Store.Load(f.Root) is null);
});
await Run("新規導入と不明配置を分ける", async () =>
{
    var f = New(); var fresh = Path.Combine(f.Base, "fresh"); f.Registration = null;
    Check(f.Validator.Validate(fresh, false, true).Fresh);
    Directory.CreateDirectory(fresh); File.WriteAllText(Path.Combine(fresh, "unknown.txt"), "keep");
    await Rejected(() => Done(() => f.Validator.Validate(fresh, false, true)));
});
await Run("更新準備・取消は設定と外部ツールを保持", async () =>
{
    var f = New(); await f.Update.Prepare(f.Root, f.Payload);
    f.InstallPayload();
    f.Update.Recover(f.Root);
    Check(f.Store.Load(f.Root)?.Phase == "recovered");
    Check(File.ReadAllText(Path.Combine(f.Root, "SSCapture.exe")) == "old");
    Check(!File.Exists(Path.Combine(f.Root, "tool.json")));
    f.CheckData();
});
await Run("更新後照合と旧所有物の整理", async () =>
{
    var f = New(); await f.Update.Prepare(f.Root, f.Payload);
    f.InstallPayload();
    f.Registration = f.Registration! with { Version = "0.1.3", Name = "SSCapture 0.1.3" };
    f.Identity = new("SSCapture", "0.1.3", "0.1.3.0", "0.1.3.0");
    f.Update.Commit(f.Root);
    Check(f.Store.Load(f.Root)?.Phase == "completed");
    Check(!File.Exists(Path.Combine(f.Root, "old-runtime.txt")));
    f.CheckData();
});
await Run("配置後失敗で削除補助も旧版へ復元", async () =>
{
    var f = New(); var uninstaller = Path.Combine(f.Root, "unins000.exe");
    var oldHash = SafeFiles.Hash(uninstaller);
    await f.Update.Prepare(f.Root, f.Payload);
    f.InstallPayload(); File.WriteAllText(uninstaller, "new uninstaller");
    File.WriteAllText(Path.Combine(f.Root, "unins000.dat"), "new metadata");
    f.Update.ObserveInstalled(f.Root);
    f.Update.Recover(f.Root);
    Check(SafeFiles.Hash(uninstaller) == oldHash);
    Check(!File.Exists(Path.Combine(f.Root, "unins000.dat")));
    Check(f.Store.Load(f.Root)?.Phase == "recovered"); f.CheckData();
});
await Run("配置確認後に外部変更された削除補助を上書きしない", async () =>
{
    var f = New(); await f.Update.Prepare(f.Root, f.Payload); f.InstallPayload();
    f.Update.ObserveInstalled(f.Root);
    File.WriteAllText(Path.Combine(f.Root, "unins000.exe"), "external change");
    await Rejected(() => Done(() => f.Update.ObserveInstalled(f.Root)));
    await Rejected(() => Done(() => f.Update.Recover(f.Root)));
    Check(File.ReadAllText(Path.Combine(f.Root, "SSCapture.exe")) == "new");
});
await Run("復旧時の外部変更を上書きしない", async () =>
{
    var f = New(); await f.Update.Prepare(f.Root, f.Payload);
    f.InstallPayload(); File.WriteAllText(Path.Combine(f.Root, "SSCapture.exe"), "external");
    await Rejected(() => Done(() => f.Update.Recover(f.Root)));
    Check(File.ReadAllText(Path.Combine(f.Root, "SSCapture.exe")) == "external");
});
await Run("退避の改変と未完了の重複開始を拒否", async () =>
{
    var f = New(); await f.Update.Prepare(f.Root, f.Payload);
    await Rejected(() => f.Update.Prepare(f.Root, f.Payload));
    var journal = f.Store.Load(f.Root)!;
    File.WriteAllText(f.Store.Backup(journal, "SSCapture.exe"), "changed backup");
    await Rejected(() => Done(() => f.Update.Recover(f.Root)));
});
await Run("所有不明の同名READMEは上書きしない", async () =>
{
    var f = New(); File.WriteAllText(Path.Combine(f.Root, "README.md"), "user text");
    await Rejected(() => f.Update.Prepare(f.Root, f.Payload));
    Check(File.ReadAllText(Path.Combine(f.Root, "README.md")) == "user text");
});
await Run("越境・利用者dataを目録から拒否", async () =>
{
    var f = New();
    foreach (var path in new[] { "../outside.json", "data/settings.json", "tools/ffmpeg.exe", @"C:\outside.exe",
        "data /settings.json", "other./file.json", "NUL.json", "test*/file.json" })
    {
        SafeFiles.Write(Path.Combine(f.Payload, "payload-manifest.json"), new PayloadManifest(1, "ss-capture", "0.1.3", [new(path, 0, new string('a', 64))]));
        await Rejected(() => f.Update.Prepare(f.Root, f.Payload));
    }
});
await Run("本体実検出器が正規定義とEXEを要求", () => Done(() =>
{
    var f = New();
    var detector = new InstallationDetector(_ => new(f.Root));
    Check(detector.Detect(TargetValidator.Definition).State == "確認できない");
}));
await Run("所有予約だけを整理し取消で復元", async () =>
{
    var f = New(); await f.Update.Prepare(f.Root, f.Payload); f.InstallPayload();
    f.Registration = f.Registration! with { Version = "0.1.3", Name = "SSCapture" };
    f.Identity = new("SSCapture", "0.1.3", "0.1.3.0", "0.1.3.0"); f.Update.Commit(f.Root);
    var settings = new HostSettingsStore(Path.Combine(f.Base, "host"));
    var gateway = new FakeGateway();
    var sid = WindowsIdentity.GetCurrent().User!.Value;
    var value = new HostSettings { HostStartup = true, StartupModes = new() { ["ss-capture"] = "direct_task", ["other"] = "direct_task" } };
    var name = "KotorinToys." + sid + ".ss-capture";
    var xml = $"<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>KotorinToys:{value.OwnerToken}:ss-capture</Description></RegistrationInfo></Task>";
    var task = new ScheduledTaskState(name, xml);
    value.OwnedTasks["ss-capture"] = new(name, task.Fingerprint, xml);
    gateway.Tasks[name] = task; gateway.Tasks["unrelated"] = new("unrelated", "<Task/>");
    settings.Save(value);
    var removal = new RemovalTransaction(f.Validator, _ => Task.CompletedTask, f.Store, settings, gateway);
    await removal.Prepare(f.Root);
    Check(gateway.Tasks.ContainsKey("unrelated") && !gateway.Tasks.ContainsKey(name));
    Check(settings.Load().StartupModes["other"] == "direct_task");
    // 削除途中でEXEがなくても、当該実行の記録を照合して予約を戻せる。
    File.Delete(Path.Combine(f.Root, "SSCapture.exe"));
    removal.Recover(f.Root);
    Check(gateway.Tasks[name].Fingerprint == task.Fingerprint);
    Check(settings.Load().StartupModes["ss-capture"] == "direct_task");
    f.CheckData();
});
Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Fixture
{
    public string Base { get; }
    public string Root => Path.Combine(Base, "導入先 空白");
    public string Payload => Path.Combine(Base, "payload");
    public Registration? Registration;
    public ExecutableIdentity Identity = new("SSCapture", "0.1.2", "0.1.2.0", "0.1.2.0");
    public TargetValidator Validator { get; }
    public OperationStore Store { get; }
    public UpdateTransaction Update { get; }
    public Fixture(string path)
    {
        Base = path; Directory.CreateDirectory(Root); Directory.CreateDirectory(Payload);
        foreach (var file in new[] { "SSCapture.exe", "SSCapture.dll", "old-runtime.txt" }) File.WriteAllText(Path.Combine(Root, file), "old");
        var old = new PayloadManifest(1, "ss-capture", "0.1.2", Directory.GetFiles(Root).Select(p => new PayloadFile(Path.GetFileName(p), new FileInfo(p).Length, SafeFiles.Hash(p))).ToArray());
        File.WriteAllText(Path.Combine(Root, "unins000.exe"), "uninstaller fixture");
        Registration = new(Root, "SSCapture 0.1.2", "0.1.2", "\"" + Path.Combine(Root, "unins000.exe") + "\"");
        Directory.CreateDirectory(Path.Combine(Root, "data")); Directory.CreateDirectory(Path.Combine(Root, "tools"));
        File.WriteAllText(Path.Combine(Root, "data", "settings.json"), "user settings");
        File.WriteAllText(Path.Combine(Root, "tools", "ffmpeg.exe"), "user media");
        foreach (var file in new[] { "SSCapture.exe", "SSCapture.dll", "README.md" }) File.WriteAllText(Path.Combine(Payload, file), "new");
        SafeFiles.Write(Path.Combine(Payload, "tool.json"), new { schemaVersion = 1, id = "ss-capture", name = "SSCapture", appId = "KotorinToys.SSCapture", version = "0.1.3", installedExecutable = "SSCapture.exe" });
        var next = new PayloadManifest(1, "ss-capture", "0.1.3", Directory.GetFiles(Payload).Select(p => new PayloadFile(Path.GetFileName(p), new FileInfo(p).Length, SafeFiles.Hash(p))).ToArray());
        SafeFiles.Write(Path.Combine(Payload, "payload-manifest.json"), next);
        Validator = new(() => Registration, _ => Identity, old);
        Store = new(Root, Path.Combine(Base, "private-state"));
        Update = new(Validator, _ => Task.CompletedTask, Store);
    }
    public void InstallPayload() { foreach (var file in Directory.GetFiles(Payload)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)), true); }
    public void CheckData()
    {
        if (File.ReadAllText(Path.Combine(Root, "data", "settings.json")) != "user settings" || File.ReadAllText(Path.Combine(Root, "tools", "ffmpeg.exe")) != "user media")
            throw new Exception("利用者データが変わった");
    }
}
sealed class FakeGateway : IStartupGateway
{
    public readonly Dictionary<string, ScheduledTaskState> Tasks = new();
    public ScheduledTaskState? Read(string name) => Tasks.GetValueOrDefault(name);
    public ScheduledTaskState Create(TaskPlan plan) => throw new NotSupportedException();
    public ScheduledTaskState Restore(string name, string xml, string securityDescriptor = "") => Tasks[name] = new(name, xml, securityDescriptor);
    public void Delete(string name, string expectedFingerprint)
    {
        if (Tasks.TryGetValue(name, out var value) && value.Fingerprint != expectedFingerprint) throw new IOException("予約が外部変更");
        Tasks.Remove(name);
    }
}
