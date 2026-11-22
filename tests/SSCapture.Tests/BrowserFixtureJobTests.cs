using System.Diagnostics;
using System.Globalization;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunBrowserJobTests(string scratch)
    {
        string root = Directory.CreateDirectory(Path.Combine(scratch, "browser-job-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (bool fail in new[] { false, true })
            {
                string directory = Directory.CreateDirectory(Path.Combine(root, fail ? "failure" : "normal")).FullName;
                int[]? ids = null;
                try
                {
                    using var job = new BrowserFixtureJob();
                    StartJobProbe(job, "parent", directory);
                    ids = ReadProbeIds(directory);
                    Check(ids.All(job.Contains), "検証ジョブに親子PIDが入っていません。");
                    if (fail) throw new OperationCanceledException("検証失敗を模擬");
                }
                catch (OperationCanceledException) when (fail) { }
                CheckProbeExited(ids!);
                Console.WriteLine(fail ? "PASS 検証失敗後も親子プロセスが終了" : "PASS 正常終了時に親子プロセスが終了");
            }

            string abrupt = Directory.CreateDirectory(Path.Combine(root, "abrupt")).FullName;
            using var owner = Process.Start(ProbeStartInfo("owner", abrupt))!;
            try
            {
                int[] ids = ReadProbeIds(abrupt);
                owner.Kill(); // Terminate only the test runner. Windows must clean up the job members.
                Check(owner.WaitForExit(5000), "検証ランナーを強制終了できません。");
                CheckProbeExited(ids);
            }
            finally { if (!owner.HasExited) owner.Kill(); }
            Console.WriteLine("PASS ランナーを強制終了しても親子プロセスが残らない");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static int RunBrowserJobProbe(string role, string directory)
    {
        switch (role)
        {
            case "owner":
                using (var job = new BrowserFixtureJob())
                {
                    StartJobProbe(job, "parent", directory);
                    Thread.Sleep(Timeout.Infinite);
                }
                break;
            case "parent":
                using (Process.Start(ProbeStartInfo("child", directory))) WriteProbePid();
                Thread.Sleep(Timeout.Infinite);
                break;
            case "child":
                WriteProbePid();
                Thread.Sleep(Timeout.Infinite);
                break;
        }
        return 0;

        void WriteProbePid() => File.WriteAllText(Path.Combine(directory, role + ".pid"),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    private static ProcessStartInfo ProbeStartInfo(string role, string directory)
    {
        string executable = Environment.ProcessPath!;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in new[] { "--browser-job-probe", role, directory }) info.ArgumentList.Add(argument);
        return info;
    }

    private static void StartJobProbe(BrowserFixtureJob job, string role, string directory)
    {
        ProcessStartInfo info = ProbeStartInfo(role, directory);
        job.Start(info.FileName, info.ArgumentList.ToArray(), directory);
    }

    private static int[] ReadProbeIds(string directory)
    {
        int Read(string role)
        {
            string path = Path.Combine(directory, role + ".pid");
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (File.Exists(path) && int.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, out int pid))
                    return pid;
                Thread.Sleep(25);
            }
            throw new TimeoutException("検証用の親子プロセスを開始できませんでした。");
        }
        return [Read("parent"), Read("child")];
    }

    private static void CheckProbeExited(int[] ids)
    {
        foreach (int id in ids)
        {
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (ArgumentException) { continue; }
            using (process) Check(process.WaitForExit(5000), $"検証用プロセス {id} が残っています。");
        }
    }
}
