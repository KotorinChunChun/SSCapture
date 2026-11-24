using System.Runtime.InteropServices;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static int RunBrowserControlSmoke(string browser, string scratch, bool frozen = false)
    {
        string marker = "SSCapture DOM " + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(Path.GetFullPath(scratch), marker);
        Directory.CreateDirectory(directory);
        string page = Path.Combine(directory, "control.html");
        File.WriteAllText(page, $$"""
            <!doctype html><title>{{marker}}</title><div style="display:flex;gap:12px">
            <button id="RootButton" autofocus style="width:190px;height:75px;background:#2664c2;forced-color-adjust:none"><span>Save item</span></button>
            <iframe title="Embedded page" srcdoc="<button id='NestedButton' style='width:170px;height:65px'><span>Open details</span></button>"></iframe></div>
            <script>
            const button = document.getElementById('RootButton');
            button.addEventListener('click', () => {
                setTimeout(() => {
                    button.style.background = '#de7617';
                    button.firstElementChild.textContent = 'Updated';
                    document.title += ' updated';
                }, 2100);
            }, { once: true });
            </script>
            """);
        try
        {
            using var browserJob = new BrowserFixtureJob();
            browserJob.Start(browser, ["--user-data-dir=" + Path.Combine(directory, "profile"), "--guest",
                "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-background-mode",
                "--disable-extensions", "--new-window", new Uri(page).AbsoluteUri], directory);
            CaptureWindow? target = null;
            Rectangle desktop = CaptureNative.MonitorBounds().Aggregate(Rectangle.Union);
            for (int i = 0; i < 60 && target is null; i++)
            {
                Thread.Sleep(125);
                target = CaptureNative.CaptureWindows(desktop).FirstOrDefault(window => window.Title.Contains(marker, StringComparison.Ordinal)
                    && browserJob.Contains((int)window.ProcessId));
            }
            if (target is null) throw new InvalidOperationException("ブラウザーの検証ページを開けませんでした。");
            NativeSmokeInput.FocusFixture(target.Handle);
            double scale = GetDpiForWindow(target.Handle) / 96.0;
            Check(scale > 0, "検証用ブラウザーの表示倍率を取得できません。");
            using var client = new CaptureContextClient();
            var first = CheckControl(95, 190, 75, "ページ内のボタン");
            CheckControl(282, 170, 65, "iframe内のボタン");
            if (frozen) return RunFrozenBrowserCapture(target, first.Bounds, directory);
            Console.WriteLine("BROWSER CONTROL PASS ページとiframeのボタンをCSS寸法と照合");
            return 0;

            ControlHit CheckControl(int x, int width, int height, string description)
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
                        return hit;
                    }
                }
                WindowCaptureFrame diagnostic = IsolatedWindowCapture.TakeFrameAsync(target, false).GetAwaiter().GetResult();
                using (diagnostic.Image) diagnostic.Image.Save(Path.Combine(directory, "browser-window.png"));
                throw new InvalidOperationException($"{description}の範囲を取得できません。倍率={scale}、画像={directory}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
