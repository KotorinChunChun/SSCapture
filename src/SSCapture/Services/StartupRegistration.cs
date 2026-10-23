using System.Diagnostics;
using System.Text.Json;

namespace SSCapture.Services;

internal sealed record StartupRegistrationState(bool Enabled, bool CanChange, string Detail);
internal interface IStartupRegistration
{
    Task<StartupRegistrationState> ReadAsync();
    Task<string?> SetAsync(bool enabled);
}

internal sealed class StartupRegistration : IStartupRegistration
{
    public async Task<StartupRegistrationState> ReadAsync()
    {
        try
        {
            string json = await InvokeAsync("startup-status");
            return JsonSerializer.Deserialize<StartupRegistrationState>(json, Options) ?? throw new IOException("自動起動の応答が空です。");
        }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }
    public async Task<string?> SetAsync(bool enabled)
    {
        try
        {
            using var response = JsonDocument.Parse(await InvokeAsync(enabled ? "startup-enable" : "startup-disable"));
            return response.RootElement.GetProperty("success").GetBoolean() ? null : response.RootElement.GetProperty("detail").GetString() ?? "予約を変更できませんでした。";
        }
        catch (Exception ex) { return ex.Message; }
    }
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    static async Task<string> InvokeAsync(string operation)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "KotorinToys.Maintenance.exe");
        if (!File.Exists(helper)) throw new IOException("自動起動の設定には正式インストール版が必要です。");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--product", "SSCapture", "--root", AppContext.BaseDirectory, "--operation", operation }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("予約の管理処理を開始できません。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // タイムアウトで管理補助を強制停止しない。次回表示時にjournalを再確認する。
        await process.WaitForExitAsync(timeout.Token);
        string json = await output;
        string failure = await error;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(json)) throw new IOException(failure.Trim());
        return json;
    }
}
