using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static string BrowserTextFixture(string title, string kind)
    {
        bool compact = kind.EndsWith("-compact", StringComparison.Ordinal);
        string content = kind switch
        {
            "span" => """<span id="target">Standalone text</span>""",
            "label" => """<label id="target" for="field">Field name</label><input id="field" value="Input value">""",
            "span-compact" => """<span id="target">ID</span>""",
            "label-compact" => """<label id="target" for="field">ID</label><input id="field" value="Input value">""",
            "button" => """<button id="target"><span>Button text</span></button>""",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return $$"""
            <!doctype html><meta charset="utf-8"><title>{{title}}</title>
            <style>
            body { margin: 0; background: white; user-select: none; }
            main { position: absolute; left: {{(compact ? 8 : 100)}}px; top: {{(compact ? 7 : 55)}}px; }
            #target { font: {{(compact ? 16 : 28)}}px monospace; color: white; background: #2664c2; forced-color-adjust: none; }
            button#target { width: 246px; height: 68px; border: 0; padding: 0; }
            input { margin-left: 12px; }
            </style>
            <main>{{content}}</main>
            <script>
            document.querySelector('#target').addEventListener('click', event => event.preventDefault());
            const poll = setInterval(() => {
                const script = document.createElement('script');
                script.src = 'change-target.js?' + Date.now();
                script.onload = script.onerror = () => script.remove();
                document.head.append(script);
            }, 100);
            function changeFixtureColor() {
                clearInterval(poll);
                document.querySelector('#target').style.background = '#de7617';
                document.title += ' updated';
            }
            </script>
            """;
    }

    private static PixelBounds FindTextFixtureBounds(CaptureWindow target, double scale, CaptureContextClient client)
    {
        CaptureNative.SynchronizeDesktop();
        Rectangle window = CaptureNative.RequireCaptureWindow(target);
        Rectangle sample = new(window.Location,
            new Size(Math.Min(window.Width, (int)(460 * scale)), Math.Min(window.Height, (int)(290 * scale))));
        int blue = Color.FromArgb(0x26, 0x64, 0xc2).ToArgb();
        Rectangle pixels = Rectangle.Empty;
        for (int attempt = 0; attempt < 10 && pixels.IsEmpty; attempt++)
        {
            Thread.Sleep(100);
            using Bitmap image = InstantCapture.Take(sample);
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).ToArgb() == blue)
                {
                    var point = new Rectangle(window.X + x, window.Y + y, 1, 1);
                    pixels = pixels.IsEmpty ? point : Rectangle.Union(pixels, point);
                }
        }
        if (!CaptureGeometry.IsUsable(pixels)) throw new InvalidOperationException("テスト対象の色の範囲が見つかりません。");
        var pointAtCenter = pixels.Location + new Size(pixels.Width / 2, pixels.Height / 2);
        var request = new ControlLookup(target.Handle.ToInt64(), target.ProcessId, PixelBounds.From(window), PixelPoint.From(pointAtCenter));
        var layout = client.FindFrozenControlLayoutAsync(request with { Point = null }, 1000).GetAwaiter().GetResult();
        var live = client.FindControlAsync(request).GetAwaiter().GetResult();
        Console.WriteLine($"Reference {pixels}; live {live?.Bounds}; provider {layout?.Entries.Length} candidates; incomplete={layout?.Incomplete}");
        if (layout is not null) PrintTextFixtureCandidates(layout, pointAtCenter);
        return PixelBounds.From(pixels);
    }

    private static void PrintTextFixtureCandidates(ControlLayout layout, Point point)
    {
        foreach (var entry in layout.Entries)
        {
            var area = entry.HitArea;
            if (entry.InDocument || new Rectangle(area.X, area.Y, area.Width, area.Height).Contains(point))
                Console.WriteLine($"  {entry.Hit.Bounds} path={entry.TreePath} snapshot={entry.SnapshotId} doc={entry.InDocument} interactive={entry.Interactive} text={entry.StaticText}");
        }
    }
}
