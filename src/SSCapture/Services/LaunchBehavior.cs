namespace SSCapture.Services;

internal static class LaunchBehavior
{
    // 自動起動・管理起動では、--settingsが同時にあっても画面を出さない。
    internal static bool ShowSettings(string[] args) => !args.Contains("--startup") && !args.Contains("--hidden");
}
