using System.Reflection;
using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureCommandTests()
    {
        Run("撮影CLIは通常起動と内部workerを変更しない", () =>
        {
            foreach (string[] args in new[] { Array.Empty<string>(), new[] { "--startup" }, new[] { "--hidden" }, new[] { "--capture-context-worker" }, new[] { "--ocr-worker" } })
                Check(!CaptureCommandLine.Handles(args), "通常起動をCLIへ取り込んだ");
            foreach (string[] args in new[] { new[] { "--capture-select" }, new[] { "--capture-region", "0", "0", "1", "1" }, new[] { "--input-preset", "x" }, new[] { "--capture-invalid" } })
                Check(CaptureCommandLine.Handles(args), "CLI引数を通常起動へ渡した");
        });
        Run("撮影CLIは負座標とプリセットIDを順序に依存せず保持", () =>
        {
            var parsed = CaptureCommandLine.Parse(["--output-preset", "出力 '$()", "--capture-region", "-1920", "-240", "640", "480", "--input-preset", "input"]);
            Equal(new Rectangle(-1920, -240, 640, 480), parsed.Region!.Value);
            Equal("input", parsed.InputPresetId); Equal("出力 '$()", parsed.OutputPresetId);
            Equal<Rectangle?>(null, CaptureCommandLine.Parse(["--capture-select"]).Region);
        });
        string[][] invalid = [[], ["--capture-select", "--capture-select"], ["--capture-select", "--capture-region", "0", "0", "1", "1"],
            ["--capture-region", "0", "0", "1"], ["--capture-region", "0", "a", "1", "1"],
            ["--capture-region", "0", "0", "0", "1"], ["--capture-region", "0", "0", "1", "-1"],
            ["--capture-region", "0", "0", "32769", "1"], ["--capture-region", "0", "0", "20000", "20000"],
            ["--capture-region", "2147483647", "0", "2", "1"], ["--capture-region", "0", "2147483647", "1", "2"],
            ["--capture-region", "2147483648", "0", "1", "1"], ["--capture-select", "--unknown"], ["--capture-select", "--startup"],
            ["--capture-select", "--input-preset"], ["--capture-select", "--input-preset", ""],
            ["--capture-select", "--input-preset", "--output-preset"], ["--capture-select", "--output-preset", "a", "--output-preset", "b"],
            ["--capture-select", "--input-preset", "a", "--input-preset", "b"], ["--input-preset", "a"], ["--capture-select", "extra"]];
        foreach (string[] args in invalid)
            Run("撮影CLIは不正引数を拒否: " + string.Join(" ", args), () => CaptureCommandReject(() => CaptureCommandLine.Parse(args)));
        Run("PowerShell生成はパス・ID・負座標を単一引用符で保護", () =>
        {
            Equal("& 'C:\\tools\\Chun''s $folder\\SSCapture.exe' '--capture-select' '--input-preset' 'in''; $(echo x)' '--output-preset' 'out & name'",
                CaptureCommandLine.Generate(@"C:\tools\Chun's $folder\SSCapture.exe", new(InputPresetId: "in'; $(echo x)", OutputPresetId: "out & name")));
            Equal("& 'C:\\SSCapture.exe' '--capture-region' '-10' '-20' '30' '40'",
                CaptureCommandLine.Generate(@"C:\SSCapture.exe", new(new(-10, -20, 30, 40))));
            CaptureCommandReject(() => CaptureCommandLine.Generate("SSCapture.exe", new()));
            CaptureCommandReject(() => CaptureCommandLine.Generate(@"C:\SSCapture.cmd", new()));
        });
        Run("CLI設定解決は指定IDの取得・出力条件を維持し設定を変更しない", () =>
        {
            AppSettings original = CaptureCommandSettings();
            var input = SavedRegion.FromSettings("追加入力", original) with { Id = "chosen-input", IncludeCursor = true, PaddingPixels = 7 };
            var output = CapturePreset.FromSettings("追加出力", original) with { Id = "chosen-output", Destination = OutputDestination.Pinned };
            original = original with { SavedRegions = [.. original.SavedRegions, input], CapturePresets = [.. original.CapturePresets, output] };
            var resolved = CaptureCommandLine.Resolve(new(new(-10, -10, 20, 30), input.Id, output.Id), original);
            Check(resolved.Settings.IncludeCursor, "入力条件を失った"); Equal(7, resolved.Settings.ScreenshotPaddingPixels);
            Equal(OutputDestination.Pinned, resolved.Settings.DefaultDestination);
            Equal(input.Id, resolved.InputPreset.Id); Equal(new Rectangle(-10, -10, 20, 30), resolved.Request.Region!.Value);
            Equal(OutputDestination.Clipboard, original.DefaultDestination); Check(!original.IncludeCursor, "既定入力を書き換えた");
            Check(!ReferenceEquals(original.SavedRegions, resolved.Settings.SavedRegions), "設定配列を共有した");
        });
        Run("CLI設定解決は不明IDと出力なしを撮影前に拒否", () =>
        {
            AppSettings settings = CaptureCommandSettings();
            CaptureCommandReject(() => CaptureCommandLine.Resolve(new(InputPresetId: "missing"), settings));
            CaptureCommandReject(() => CaptureCommandLine.Resolve(new(OutputPresetId: "missing"), settings));
            settings = settings with { CapturePresets = settings.CapturePresets.Select(p => p.IsDefault ? p with { Destination = OutputDestination.None } : p).ToArray() };
            CaptureCommandReject(() => CaptureCommandLine.Resolve(new(), settings));
        });
        Run("選択CLIは座標付き入力と全画面入力を即時撮影にしない", () =>
        {
            AppSettings settings = CaptureCommandSettings();
            var coordinate = RegionCoordinates.FromBounds("座標入力", new(-100, 20, 40, 30),
                [new("test-display", "テスト画面", new(-200, 0, 400, 300), 96, 96)]) with { Id = "fixed", IncludeCursor = true };
            settings = settings with { SavedRegions = [.. settings.SavedRegions, coordinate] };
            var resolved = CaptureCommandLine.Resolve(new(InputPresetId: coordinate.Id), settings);
            var options = resolved.SelectionOptions();
            Equal(CaptureMode.Rectangle, options.Mode); Check(!options.InitialInputPreset!.UseFixedCoordinates, "登録座標を選択へ渡した");
            Equal("fixed", options.InitialInputPreset.Id); Check(options.IncludeCursor, "カーソル条件を失った");
            var all = SavedRegion.FromSettings("全画面", settings with { DefaultMode = CaptureMode.FullScreen }) with { Id = "all" };
            Equal(CaptureMode.Rectangle, CaptureCommandLine.Resolve(new(InputPresetId: all.Id), settings with { SavedRegions = [.. settings.SavedRegions, all] }).SelectionOptions().Mode);
        });
        Run("CLIの設定読込はファイルもバックアップも書き換えない", () => WithOutputDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            store.Save(CaptureCommandSettings());
            byte[] before = File.ReadAllBytes(path);
            DateTime time = File.GetLastWriteTimeUtc(path);
            var loaded = store.Load(out string? warning);
            Equal<string?>(null, warning);
            _ = CaptureCommandLine.Resolve(new(), loaded);
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "設定ファイルを変更した");
            Equal(time, File.GetLastWriteTimeUtc(path)); Equal(1, Directory.GetFiles(directory).Length);
        }));
        Run("CLI固定撮影は明示範囲と注入画像を1回だけ配送", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int takes = 0, copies = 0, selections = 0;
            var command = CaptureCommandLine.Resolve(new(new(-30, -40, 6, 4)), CaptureCommandSettings());
            using var output = new OutputRouter(directory, bitmap =>
            {
                copies++; Equal(Color.Red.ToArgb(), bitmap.GetPixel(2, 1).ToArgb()); return Task.CompletedTask;
            }, readDisplays: () => []);
            int result = await CaptureCommandRunner.ExecuteAsync(command, output, (bounds, settings) =>
            {
                takes++; Equal(command.Request.Region!.Value, bounds);
                var image = new Bitmap(6, 4); image.SetPixel(2, 1, Color.Red);
                return Task.FromResult<(Bitmap, CaptureMetadata?)>((image, null));
            }, options => { selections++; throw new InvalidOperationException("選択を起動した"); });
            Equal(0, result); Equal(1, takes); Equal(1, copies); Equal(0, selections); Equal(0, Directory.GetFiles(directory).Length);
        })));
        Run("CLI選択撮影は結果画像と変更後の出力条件を配送", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int selections = 0, pins = 0, copies = 0;
            using var output = new OutputRouter(directory, _ => { copies++; return Task.CompletedTask; }, readDisplays: () => [],
                pinImage: (image, bounds, settings) => { pins++; Equal(new Rectangle(-20, 30, 6, 4), bounds); Equal(6, image.Width); });
            int result = await CaptureCommandRunner.ExecuteAsync(CaptureCommandLine.Resolve(new(), CaptureCommandSettings()), output,
                (_, _) => throw new InvalidOperationException("即時撮影した"), options =>
                {
                    selections++;
                    return Task.FromResult(new CaptureOutcome(new Bitmap(6, 4), new(new(-20, 30, 6, 4), null, OutputDestination.Pinned),
                        outputSettings: options.OutputSettings));
                });
            Equal(0, result); Equal(1, selections); Equal(1, pins); Equal(0, copies);
        })));
        Run("CLIは外部アプリ出力を維持し実アプリ起動へ触れない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            string editor = Path.Combine(directory, "fixture.exe"); File.WriteAllBytes(editor, [0]);
            AppSettings settings = CaptureCommandSettings();
            var app = new ExternalApplication { Id = "fixture", Name = "テスト", Executable = editor };
            settings = settings with { ImageEditors = [app], SelectedImageEditorId = app.Id,
                CapturePresets = settings.CapturePresets.Select(p => p.IsDefault ? p with { Destination = OutputDestination.ExternalApp, ImageEditorId = app.Id } : p).ToArray() };
            int launches = 0;
            using var output = new OutputRouter(directory, launch: info => { launches++; Equal(editor, info.FileName); }, readDisplays: () => []);
            Equal(0, await CaptureCommandRunner.ExecuteAsync(CaptureCommandLine.Resolve(new(new(0, 0, 4, 3)), settings), output,
                (_, _) => Task.FromResult<(Bitmap, CaptureMetadata?)>((new Bitmap(4, 3), null))));
            Equal(1, launches);
        })));
        Run("CLIは選択キャンセルを終了コード2として無出力で終了", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int copies = 0;
            using var output = new OutputRouter(directory, _ => { copies++; return Task.CompletedTask; }, readDisplays: () => []);
            Equal(2, await CaptureCommandRunner.ExecuteAsync(CaptureCommandLine.Resolve(new(), CaptureCommandSettings()), output,
                select: _ => Task.FromResult(new CaptureOutcome(null, null))));
            Equal(0, copies); Equal(0, Directory.GetFiles(directory).Length);
        })));
        Run("CLIは取得失敗・出力失敗を終了コード1として報告", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int errors = 0;
            var command = CaptureCommandLine.Resolve(new(new(0, 0, 4, 3)), CaptureCommandSettings());
            using var output = new OutputRouter(directory, _ => throw new IOException("注入コピー失敗"), readDisplays: () => []);
            Equal(1, await CaptureCommandRunner.ExecuteAsync(command, output, (_, _) => throw new IOException("注入取得失敗"),
                report: (_, error) => { if (error) errors++; }));
            Equal(1, await CaptureCommandRunner.ExecuteAsync(command, output,
                (_, _) => Task.FromResult<(Bitmap, CaptureMetadata?)>((new Bitmap(4, 3), null)), report: (_, error) => { if (error) errors++; }));
            Equal(2, errors);
        })));
        Run("CLIは選択結果の出力OFFを成功や既定出力へ読み替えない", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            int copies = 0;
            using var output = new OutputRouter(directory, _ => { copies++; return Task.CompletedTask; }, readDisplays: () => []);
            Equal(1, await CaptureCommandRunner.ExecuteAsync(CaptureCommandLine.Resolve(new(), CaptureCommandSettings()), output,
                select: options => Task.FromResult(new CaptureOutcome(new Bitmap(4, 3), new(new(0, 0, 4, 3), null, OutputDestination.None)))));
            Equal(0, copies);
        })));
        Run("コマンドUIは生成だけで実行せず注入先へ文字列をコピー", () =>
        {
            string? copied = null;
            using var control = new CaptureCommandControl(CaptureCommandSettings, (_, error) => Check(!error, "生成に失敗"), @"C:\SSCapture.exe",
                value => { copied = value; return Task.CompletedTask; });
            CaptureCommandClick(control, "CaptureCommandGenerate");
            var text = (TextBox)control.Controls.Find("CaptureCommandText", true).Single();
            Equal("& 'C:\\SSCapture.exe' '--capture-select'", text.Text); Check(text.ReadOnly && text.Multiline, "コマンド欄の編集が可能");
            Equal<string?>(null, copied);
            CaptureCommandClick(control, "CaptureCommandCopy"); Equal(text.Text, copied);
            var method = (ComboBox)control.Controls.Find("CaptureCommandMethod", true).Single();
            var x = (NumericUpDown)control.Controls.Find("CaptureCommandX", true).Single();
            Check(!x.Enabled, "選択方式で座標が有効");
            method.SelectedIndex = 1; Check(x.Enabled, "固定方式で座標が無効"); x.Value = -200;
            ((NumericUpDown)control.Controls.Find("CaptureCommandWidth", true).Single()).Value = 8;
            ((NumericUpDown)control.Controls.Find("CaptureCommandHeight", true).Single()).Value = 6;
            CaptureCommandClick(control, "CaptureCommandGenerate");
            Equal("& 'C:\\SSCapture.exe' '--capture-region' '-200' '0' '8' '6'", text.Text);
        });
        Run("コマンドUIは候補の並べ替えでIDを保持し削除時に誤選択しない", () =>
        {
            AppSettings settings = CaptureCommandSettings();
            var extra = SavedRegion.FromSettings("追加", settings) with { Id = "extra" };
            settings = settings with { SavedRegions = [.. settings.SavedRegions, extra] };
            using var control = new CaptureCommandControl(() => settings, (_, _) => { }, @"C:\SSCapture.exe");
            var input = (ComboBox)control.Controls.Find("CaptureCommandInput", true).Single();
            input.SelectedIndex = input.Items.Count - 1;
            settings = settings with { SavedRegions = settings.SavedRegions.Reverse().ToArray() };
            control.RefreshCandidates(); Equal("追加", input.SelectedItem!.ToString());
            CaptureCommandClick(control, "CaptureCommandGenerate");
            Check(control.Controls.Find("CaptureCommandText", true).Single().Text.Contains("'extra'", StringComparison.Ordinal), "IDを失った");
            settings = settings with { SavedRegions = settings.SavedRegions.Where(p => p.Id != extra.Id).ToArray() };
            control.RefreshCandidates(); Equal(-1, input.SelectedIndex);
            CaptureCommandClick(control, "CaptureCommandGenerate");
            Equal("", control.Controls.Find("CaptureCommandText", true).Single().Text);
        });
        Run("コマンドUIは生成時の最新設定を検証し削除済みIDを使わない", () =>
        {
            AppSettings settings = CaptureCommandSettings();
            var extra = CapturePreset.FromSettings("追加", settings) with { Id = "extra-output" };
            settings = settings with { CapturePresets = [.. settings.CapturePresets, extra] };
            int errors = 0;
            using var control = new CaptureCommandControl(() => settings, (_, error) => { if (error) errors++; }, @"C:\SSCapture.exe");
            var output = (ComboBox)control.Controls.Find("CaptureCommandOutput", true).Single();
            output.SelectedIndex = output.Items.Count - 1;
            settings = settings with { CapturePresets = settings.CapturePresets.Where(p => p.Id != extra.Id).ToArray() };
            CaptureCommandClick(control, "CaptureCommandGenerate"); Equal(1, errors);
            Equal("", control.Controls.Find("CaptureCommandText", true).Single().Text);
        });
        Run("コマンドUIは固定サイズ入力を取り込み明示座標を優先する", () =>
        {
            AppSettings settings = CaptureCommandSettings();
            var inputPreset = SavedRegion.FromSettings("取込", settings) with { Id = "import", Bounds = new(-100, -50, 640, 480) };
            settings = settings with { SavedRegions = [.. settings.SavedRegions, inputPreset] };
            using var control = new CaptureCommandControl(() => settings, (_, error) => Check(!error, "取込失敗"), @"C:\SSCapture.exe");
            ((ComboBox)control.Controls.Find("CaptureCommandMethod", true).Single()).SelectedIndex = 1;
            var input = (ComboBox)control.Controls.Find("CaptureCommandInput", true).Single();
            input.SelectedIndex = input.Items.Count - 1;
            CaptureCommandClick(control, "CaptureCommandImport");
            var x = (NumericUpDown)control.Controls.Find("CaptureCommandX", true).Single();
            Equal(-100m, x.Value); Equal(640m, ((NumericUpDown)control.Controls.Find("CaptureCommandWidth", true).Single()).Value);
            x.Value = -200;
            control.RefreshCandidates(); Equal(-200m, x.Value);
            CaptureCommandClick(control, "CaptureCommandGenerate");
            Equal("& 'C:\\SSCapture.exe' '--capture-region' '-200' '-50' '640' '480' '--input-preset' 'import'",
                control.Controls.Find("CaptureCommandText", true).Single().Text);
        });
    }

    private static AppSettings CaptureCommandSettings() => new AppSettings { NotificationDurationSeconds = 0 }.EnsurePresets();

    private static void CaptureCommandReject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "不正な撮影コマンドを受け入れた");
    }

    private static void CaptureCommandClick(Control root, string name) => typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(root.Controls.Find(name, true).Single(), [EventArgs.Empty]);
}
