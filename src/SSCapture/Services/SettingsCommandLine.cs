using System.Text.Json;
using KotorinToys.Integration;
using SSCapture.Recording;

namespace SSCapture.Services;

internal static class SettingsCommandLine
{
    internal const string SingletonName = @"Local\SSCapture.Singleton";
    internal const string ShowSettingsSignalName = @"Local\SSCapture.ShowSettings";
    internal const string ManagementId = "ss-capture";

    internal static bool Handles(string[] args) => args.FirstOrDefault() is
        "--settings-export" or "--settings-import" or "--startup-status" or "--startup-enable" or "--startup-disable";

    internal static void Run(string[] args, SettingsStore store, TextWriter output, IStartupRegistration startup,
        string singletonName = SingletonName, string managementId = ManagementId)
    {
        switch (args)
        {
            case ["--settings-export", var path] when IsPath(path):
                SettingsTransfer.Export(path, LoadCurrent(store));
                output.WriteLine("設定を書き出しました。");
                break;
            case ["--settings-import", var path, "--check"] when IsPath(path):
                var candidate = SettingsTransfer.Read(path);
                // 事前確認は破損した現設定を復旧する場合にも使える。
                var current = store.Load(out string? warning);
                if (warning is not null) output.WriteLine(warning);
                output.Write(SettingsTransfer.Preview(current, candidate, RegionCoordinates.ReadDisplays, RecordingService.GetMicrophones));
                break;
            case ["--settings-import", var path] when IsPath(path):
                Import(store, SettingsTransfer.Read(path), singletonName, managementId);
                output.WriteLine("設定を適用しました。");
                break;
            case ["--startup-status"]:
                WriteStartupStatus(startup, output);
                break;
            case ["--startup-enable"] or ["--startup-disable"]:
                string? error = startup.SetAsync(args[0] == "--startup-enable").GetAwaiter().GetResult();
                if (error is not null) throw new InvalidOperationException(error);
                WriteStartupStatus(startup, output);
                break;
            default:
                throw new ArgumentException("設定用の引数を確認してください。取込の事前確認には --settings-import <path> --check を指定します。");
        }
    }

    private static bool IsPath(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);

    private static AppSettings LoadCurrent(SettingsStore store)
    {
        var current = store.Load(out string? warning);
        if (warning is not null) throw new InvalidOperationException(warning);
        return current;
    }

    private static void WriteStartupStatus(IStartupRegistration startup, TextWriter output)
    {
        var state = startup.ReadAsync().GetAwaiter().GetResult();
        output.WriteLine(JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void Import(SettingsStore store, AppSettings candidate, string singletonName, string managementId)
    {
        using var singleton = new Mutex(false, singletonName);
        bool owned;
        try { owned = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            var payload = JsonSerializer.SerializeToElement(candidate, SettingsStore.Options);
            ManagementEndpoint.ImportSettingsAsync(managementId, payload).GetAwaiter().GetResult();
            return;
        }

        try { store.Import(candidate); }
        finally { singleton.ReleaseMutex(); }
    }
}
