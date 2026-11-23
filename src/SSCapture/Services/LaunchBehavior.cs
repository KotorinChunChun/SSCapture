using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace SSCapture.Services;

internal static class LaunchBehavior
{
    internal const string AdministratorHandoff = "--administrator-handoff";

    internal static bool IsAdministratorHandoff(string[] args) => args is [AdministratorHandoff];

    internal static bool RequestsAdministrator(string[] args, bool enabled) =>
        enabled && (args is [] or ["--settings"]);

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static string? StartAsAdministrator(string executable, Func<ProcessStartInfo, bool>? start = null)
    {
        var request = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        request.ArgumentList.Add(AdministratorHandoff);
        try
        {
            return (start ?? Launch)(request) ? null : "管理者として起動できませんでした。";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "管理者としての起動が取り消されました。";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return "管理者として起動できませんでした。 " + ex.Message;
        }

        static bool Launch(ProcessStartInfo request)
        {
            using var child = Process.Start(request);
            return child is not null;
        }
    }

    // 自動起動・管理起動では、--settingsが同時にあっても画面を出さない。
    internal static bool ShowSettings(string[] args) => !args.Contains("--startup") && !args.Contains("--hidden");
}
