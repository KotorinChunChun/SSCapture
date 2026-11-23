using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void WindowCaptureCommandTests()
    {
        var first = new CaptureWindow(new(-32, 20, 14, 11), "資料 A", 0x1234, 100, 101);
        var second = first with { Handle = 0x5678, Title = "資料 B" };
        CaptureWindow[] windows = [first, second];

        Run("ウィンドウCLIは十進・十六進のハンドルと描画領域を解析", () =>
        {
            Check(CaptureCommandLine.Handles(["--hwnd", "4660"]), "ウィンドウ引数が常駐起動へ渡った");
            var request = CaptureCommandLine.Parse(["--hwnd", "0x1234", "--client-area", "--capture-window"]);
            Equal(new WindowCaptureCommand(first.Handle, null, null, true), request.Window);
            Equal(new WindowCaptureCommand(first.Handle, null, null, false),
                CaptureCommandLine.Parse(["--capture-window", "--hwnd", "4660"]).Window);
            Equal("& 'C:\\SSCapture.exe' '--capture-window' '--hwnd' '0x1234' '--client-area'",
                CaptureCommandLine.Generate(@"C:\SSCapture.exe", request));
            Equal("& 'C:\\SSCapture.exe' '--capture-window' '--process' 'fixture.exe' '--title' '資料 A''s'",
                CaptureCommandLine.Generate(@"C:\SSCapture.exe",
                    CaptureCommandLine.Parse(["--capture-window", "--process", "fixture.exe", "--title", "資料 A's"])));
        });

        string[][] invalid = [["--capture-window"], ["--capture-window", "--title", "資料 A"],
            ["--capture-window", "--hwnd", "0"], ["--capture-window", "--hwnd", "0x10000000000000000"],
            ["--capture-window", "--hwnd", "-1"], ["--capture-window", "--hwnd", "0x"],
            ["--capture-window", "--hwnd", "1", "--process", "app"], ["--capture-window", "--hwnd", "1", "--title", "資料 A"],
            ["--capture-window", "--process", "C:\\tools\\app.exe"], ["--capture-window", "--process", "app", "--title"],
            ["--capture-window", "--process", "app", "--client-area", "--client-area"],
            ["--capture-select", "--client-area"], ["--capture-preset", "--capture-window", "--hwnd", "1"]];
        foreach (var args in invalid)
            Run("ウィンドウCLIは指定誤りを拒否 " + string.Join(" ", args), () => CaptureCommandReject(() => CaptureCommandLine.Parse(args)));

        Run("ウィンドウCLIは同一プロセス内の完全一致とハンドル指定を区別", () =>
        {
            Equal(first, new WindowCaptureCommand(first.Handle, null, null, false)
                .Resolve(windows, _ => throw new InvalidOperationException("ハンドル指定でプロセス名を検索した")));
            Equal(second, new WindowCaptureCommand(null, "FIXTURE.EXE", second.Title, false).Resolve(windows, _ => "fixture"));
            Equal(first, new WindowCaptureCommand(null, "fixture", first.Title, true).Resolve(windows, _ => "fixture"));
        });

        Run("ウィンドウCLIは曖昧一致・該当なし・消滅を無出力で報告", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = CaptureCommandSettings();
            using var output = new OutputRouter(directory, _ => throw new InvalidOperationException("失敗時に出力した"), readDisplays: () => []);
            async Task CheckFailure(WindowCaptureCommand request, IReadOnlyList<CaptureWindow> candidates, string message, int expectedCaptures)
            {
                int captures = 0;
                int resolutions = 0;
                string? reason = null;
                var command = CaptureCommandLine.Resolve(new() { Window = request }, settings);
                Equal(1, await CaptureCommandRunner.ExecuteAsync(command, output,
                    report: (detail, error) => { if (error) reason = detail; },
                    resolveWindow: window => { resolutions++; return window.Resolve(candidates, _ => "fixture"); },
                    takeWindow: (_, _, _, _) => { captures++; throw new InvalidOperationException("取得直前に閉じました"); }));
                Equal(1, resolutions);
                Equal(expectedCaptures, captures);
                Check(reason?.Contains(message, StringComparison.Ordinal) == true, reason ?? "失敗理由がありません");
            }
            await CheckFailure(new(null, "fixture", null, false), windows, "複数", 0);
            await CheckFailure(new(null, "fixture", "資料", false), windows, "見つかりません", 0);
            await CheckFailure(new(null, "fixture", first.Title, false), [first, second with { Title = first.Title }], "複数", 0);
            await CheckFailure(new(second.Handle, null, null, false), windows, "取得直前に閉じました", 1);
            Equal(0, Directory.GetFiles(directory).Length);
        })));

        Run("描画領域のCLIは枠とタイトルバーを除き、出力プリセットを適用", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            Rectangle client = new(-30, 23, 10, 6);
            var settings = CaptureCommandSettings();
            var preset = settings.CapturePresets.First(p => p.IsDefault) with
            {
                Id = "fixture-output", Name = "描画領域テスト", IsDefault = false,
                Destination = OutputDestination.File, SaveDirectory = directory, FileNamePattern = "client.{ext}", WhitePaddingPixels = 2,
                OpenFolderAfterSave = false, MetadataMode = CaptureMetadataMode.None
            };
            settings = settings with { CapturePresets = [.. settings.CapturePresets, preset] };
            var request = CaptureCommandLine.Parse(["--capture-window", "--hwnd", "0x1234", "--client-area", "--output-preset", preset.Id]);
            using var output = new OutputRouter(Path.Combine(directory, "work"), readDisplays: () => []);
            string? reason = null;
            int exit = await CaptureCommandRunner.ExecuteAsync(CaptureCommandLine.Resolve(request, settings), output,
                report: (message, error) => { if (error) reason = message; },
                resolveWindow: window => window.Resolve(windows, _ => "fixture"),
                takeWindow: (target, current, isolated, clientOnly) =>
                {
                    Equal(first, target);
                    Check(isolated && clientOnly, "WGCの描画領域指定が失われた");
                    Equal(2, current.ScreenshotPaddingPixels);
                    using var original = new Bitmap(first.Bounds.Width, first.Bounds.Height);
                    using (Graphics graphics = Graphics.FromImage(original))
                    {
                        graphics.Clear(Color.Red);
                        graphics.FillRectangle(Brushes.Blue, 2, 3, client.Width, client.Height);
                    }
                    original.SetPixel(11, 8, Color.Green);
                    var image = IsolatedWindowCapture.CropClientFrame(original, first.Bounds, client);
                    return Task.FromResult(new WindowReplayFrame(image, client, null, first.Title,
                        new CaptureReplaySource.Windows([target], true, false, true) { ClientAreaOnly = true }));
                });
            Check(exit == 0, reason ?? "ウィンドウの撮影画像を出力できなかった");
            using var saved = new Bitmap(Path.Combine(directory, "client.png"));
            Equal(new Size(14, 10), saved.Size);
            Equal(Color.White.ToArgb(), saved.GetPixel(0, 0).ToArgb());
            Equal(Color.Blue.ToArgb(), saved.GetPixel(2, 2).ToArgb());
            Equal(Color.Green.ToArgb(), saved.GetPixel(11, 7).ToArgb());
        })));

        Run("WGC描画領域はフレーム不一致や範囲外を拒否", () =>
        {
            using var image = new Bitmap(first.Bounds.Width, first.Bounds.Height);
            foreach (var (frame, client) in new[] { (new Rectangle(-32, 20, 13, 11), new Rectangle(-30, 23, 10, 6)),
                (first.Bounds, new Rectangle(-30, 23, 13, 6)) })
            {
                try { using var crop = IsolatedWindowCapture.CropClientFrame(image, frame, client); }
                catch (InvalidOperationException) { continue; }
                throw new InvalidOperationException("一致しない描画領域を受け入れた");
            }
        });

        Run("再撮影は描画領域の指定と移動後のウィンドウを保持", () =>
        {
            var source = new CaptureReplaySource.Windows([first], false, false, true) { ClientAreaOnly = true };
            var recipe = LastCaptureRecipe.Create(0, first.Bounds, null, CaptureCommandSettings(), [], source, _ => 123);
            var moved = first with { Bounds = new(200, -120, 22, 16) };
            Check(recipe.ClientAreaOnly && !recipe.UseIsolatedWindow, "再撮影が取得方式を変更した");
            Equal(moved, recipe.ResolveWindows([moved, second], _ => 123).Single());
        });

        Run("描画領域メニューはウィンドウ撮影でだけ表示し、既定は全体", () =>
        {
            bool clientOnly = false;
            using var menu = new CaptureContextMenu(OutputDestination.Clipboard, _ => { }, () => { }, () => { }, CaptureMode.Window);
            menu.AddWindowAreaOptions(client =>
            {
                clientOnly = client;
                menu.SetWindowArea(CaptureMode.Window, client);
            });
            menu.SetWindowArea(CaptureMode.Window, clientOnly);
            Check(menu.Command("WindowCaptureArea").Available, "ウィンドウ撮影に撮影範囲の選択肢がない");
            Check(menu.Command("WholeWindowArea").Checked && !menu.Command("ClientWindowArea").Checked, "既定値がウィンドウ全体ではない");
            menu.Command("ClientWindowArea").PerformClick();
            Check(clientOnly && menu.Command("ClientWindowArea").Checked && !menu.Command("WholeWindowArea").Checked,
                "描画領域の選択が反映されない");
            menu.SetWindowArea(CaptureMode.Rectangle, clientOnly);
            Check(!menu.Command("WindowCaptureArea").Available, "矩形撮影にウィンドウ撮影用の項目が出た");
        });
    }
}
