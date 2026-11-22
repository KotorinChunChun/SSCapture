using System.Diagnostics;
using System.Runtime.InteropServices;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunBrowserControlSmoke(string browser, string scratch)
    {
        string marker = "SSCapture DOM " + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetFullPath(scratch), marker);
        Directory.CreateDirectory(directory);
        string page = Path.Combine(directory, "control.html");
        File.WriteAllText(page, $"""
            <!doctype html><title>{marker}</title><div style="display:flex;gap:12px">
            <button id="RootButton" style="width:190px;height:75px"><span>Save item</span></button>
            <iframe title="Embedded page" srcdoc="<button id='NestedButton' style='width:170px;height:65px'><span>Open details</span></button>"></iframe></div>
            """);
        var startup = new ProcessStartInfo(browser) { UseShellExecute = false };
        foreach (string argument in new[] { "--user-data-dir=" + Path.Combine(directory, "profile"), "--no-first-run",
            "--no-default-browser-check", "--disable-extensions", "--new-window", new Uri(page).AbsoluteUri })
            startup.ArgumentList.Add(argument);
        using var process = Process.Start(startup);
        CaptureWindow? target = null;
        try
        {
            Rectangle desktop = CaptureNative.MonitorBounds().Aggregate(Rectangle.Union);
            for (int i = 0; i < 60 && target is null; i++)
            {
                Thread.Sleep(125);
                target = CaptureNative.CaptureWindows(desktop).FirstOrDefault(window => window.Title.Contains(marker, StringComparison.Ordinal));
            }
            if (target is null) throw new InvalidOperationException("ブラウザーの検証ページを開けませんでした。");
            NativeSmokeInput.FocusFixture(target.Handle);
            double scale = GetDpiForWindow(target.Handle) / 96.0;
            Check(scale > 0, "検証用ブラウザーの表示倍率を取得できません。");
            using var client = new CaptureContextClient();
            CheckControl(95, 190, 75, "ページ内のボタン");
            CheckControl(282, 170, 65, "iframe内のボタン");
            Console.WriteLine("BROWSER CONTROL PASS ページとiframeのボタンをCSS寸法と照合");
            return 0;

            void CheckControl(int x, int width, int height, string description)
            {
                Rectangle window = CaptureNative.RequireCaptureWindow(target);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Thread.Sleep(400);
                    for (int y = 60; y <= 240; y += 12)
                    {
                        Point point = new(window.X + (int)Math.Round(x * scale), window.Y + (int)Math.Round(y * scale));
                        var request = new ControlLookup(target.Handle.ToInt64(), target.ProcessId, PixelBounds.From(window), PixelPoint.From(point));
                        ControlHit? hit = client.FindControlAsync(request).GetAwaiter().GetResult();
                        if (hit is null || Math.Abs(hit.Bounds.Width - Math.Round(width * scale)) > 1
                            || Math.Abs(hit.Bounds.Height - Math.Round(height * scale)) > 1) continue;
                        Console.WriteLine($"PASS {description} {hit.Bounds}");
                        return;
                    }
                }
                WindowCaptureFrame diagnostic = IsolatedWindowCapture.TakeFrameAsync(target, false).GetAwaiter().GetResult();
                using (diagnostic.Image) diagnostic.Image.Save(Path.Combine(directory, "browser-window.png"));
                throw new InvalidOperationException($"{description}の範囲を取得できません。倍率={scale}、画像={directory}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (target is not null)
            {
                try { using var owner = Process.GetProcessById((int)target.ProcessId); owner.CloseMainWindow(); }
                catch (ArgumentException) { }
            }
        }

    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
