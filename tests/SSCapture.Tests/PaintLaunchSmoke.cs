using System.Diagnostics;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunPaintLaunchSmoke(string root)
    {
        int successes = 0;
        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            var original = Process.GetProcessesByName("mspaint").Select(p => { using (p) return p.Id; }).ToHashSet();
            using var image = new Bitmap(120, 80);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.DodgerBlue, 10, 10, 60, 40);
            }
            foreach (bool saveFile in new[] { false, true })
            {
                string? document = null;
                Process? fixture = null;
                var before = Process.GetProcessesByName("mspaint").Select(p => { using (p) return p.Id; }).ToHashSet();
                try
                {
                    var router = new OutputRouter(Path.Combine(root, "work"), launch: info =>
                    {
                        document = info.ArgumentList.Single();
                        Check(File.Exists(document), "起動前に完成PNGがない");
                        using var started = Process.Start(info) ?? throw new InvalidOperationException("起動プロセスがない");
                    });
                    var settings = new AppSettings { SaveDirectory = Path.Combine(root, "saved"), ImageEditorPath = EditorExecutable.LegacyPaint };
                    RunOutputTask(async () => await router.ScreenshotAsync(image,
                        OutputDestination.ExternalApp | (saveFile ? OutputDestination.File : 0), settings,
                        fileName: "ペイント起動検証_保存ON_" + Guid.NewGuid().ToString("N") + ".png"));
                    Check(document is not null, "実ペイントへファイルを渡していない");
                    var timer = Stopwatch.StartNew();
                    while (fixture is null && timer.Elapsed < TimeSpan.FromSeconds(10))
                    {
                        foreach (Process process in Process.GetProcessesByName("mspaint"))
                        {
                            if (!before.Contains(process.Id) && process.MainWindowHandle != 0
                                && process.MainWindowTitle.Contains(Path.GetFileNameWithoutExtension(document!), StringComparison.OrdinalIgnoreCase))
                            { fixture = process; break; }
                            process.Dispose();
                        }
                        if (fixture is null) Thread.Sleep(100);
                    }
                    Check(fixture is not null, "検証PNGを表示する新しいペイントウィンドウを確認できない（既存文書は操作しない）");
                    using var opened = new Bitmap(document!);
                    Equal(image.Size, opened.Size);
                    Equal(Color.DodgerBlue.ToArgb(), opened.GetPixel(20, 20).ToArgb());
                    Equal(saveFile, Path.GetDirectoryName(document) == settings.SaveDirectory);
                    successes++;
                    Console.WriteLine($"PASS PAINT 保存{(saveFile ? "ON" : "OFF")} 旧設定から実ペイントへ120x80 PNGを渡して表示");
                }
                finally
                {
                    if (fixture is not null)
                    {
                        try
                        {
                            if (!fixture.HasExited)
                            {
                                Check(fixture.CloseMainWindow(), "検証用ウィンドウの正常終了を要求できない");
                                Check(fixture.WaitForExit(4000), "検証用ペイントの正常終了待機が完了しない");
                            }
                        }
                        finally { fixture.Dispose(); }
                    }
                }
            }
            foreach (int pid in original)
            {
                using var retained = Process.GetProcessById(pid);
                Check(!retained.HasExited, "開始前のペイントが終了した");
            }
            Console.WriteLine($"PAINT RESULT: {successes} passed, existing Paint processes retained");
            return successes == 2 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine("FAIL PAINT " + ex.Message); return 1; }
    }
}
