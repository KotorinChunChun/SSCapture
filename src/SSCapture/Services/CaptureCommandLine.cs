using System.Globalization;
using SSCapture.Capture;
using SSCapture.UI;

namespace SSCapture.Services;

internal sealed record CaptureCommandRequest(Rectangle? Region = null, string? InputPresetId = null, string? OutputPresetId = null);

internal sealed record ResolvedCaptureCommand(CaptureCommandRequest Request, AppSettings Settings, SavedRegion InputPreset)
{
    internal CaptureOptions SelectionOptions()
    {
        // 選択方式では登録座標や全画面設定を即時撮影へ読み替えない。
        SavedRegion input = InputPreset with
        {
            UseFixedCoordinates = false,
            Mode = InputPreset.UseFixedCoordinates || InputPreset.Mode == CaptureMode.FullScreen ? CaptureMode.Rectangle : InputPreset.Mode
        };
        return new(input.Mode, CaptureIntent.Screenshot, Settings.DefaultDestination, Settings.FreezeFrame,
            IncludeCursor: Settings.IncludeCursor, FixedSize: new(Settings.FixedWidth, Settings.FixedHeight),
            FixedAnchor: Settings.FixedCaptureAnchor, LimitToStartingMonitor: Settings.LimitToStartingMonitor,
            Grid20: Settings.Grid20, ShowSelectionGuide: Settings.ShowSelectionGuide, OutputSettings: Settings,
            DimCaptureBackground: Settings.DimCaptureBackground, InitialInputPreset: input);
    }
}

internal static class CaptureCommandLine
{
    internal static bool Handles(IReadOnlyList<string> args) => args.Any(arg =>
        arg.StartsWith("--capture-", StringComparison.Ordinal) && arg != "--capture-context-worker"
        || arg is "--input-preset" or "--output-preset");

    internal static CaptureCommandRequest Parse(IReadOnlyList<string> args)
    {
        bool mode = false;
        Rectangle? region = null;
        string? input = null, output = null;
        for (int index = 0; index < args.Count; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--capture-select":
                    if (mode) throw new ArgumentException("撮影方式は1つだけ指定してください。");
                    mode = true;
                    break;
                case "--capture-region":
                    if (mode) throw new ArgumentException("撮影方式は1つだけ指定してください。");
                    mode = true;
                    int Number()
                    {
                        if (++index >= args.Count || !int.TryParse(args[index], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value))
                            throw new ArgumentException("固定範囲にはx y width heightの整数を指定してください。");
                        return value;
                    }
                    region = new Rectangle(Number(), Number(), Number(), Number());
                    ValidateRegion(region.Value);
                    break;
                case "--input-preset":
                case "--output-preset":
                    if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal)
                        || args[index].Any(char.IsControl))
                        throw new ArgumentException(option + "にはプリセットIDを指定してください。");
                    if (option == "--input-preset")
                    {
                        if (input is not null) throw new ArgumentException("入力プリセットが重複しています。");
                        input = args[index];
                    }
                    else
                    {
                        if (output is not null) throw new ArgumentException("出力プリセットが重複しています。");
                        output = args[index];
                    }
                    break;
                default: throw new ArgumentException("不明な撮影引数です: " + option);
            }
        }
        if (!mode) throw new ArgumentException("--capture-selectまたは--capture-regionを指定してください。");
        return new(region, input, output);
    }

    internal static void ValidateRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Width > RegionCoordinates.MaximumDimension
            || region.Height > RegionCoordinates.MaximumDimension || (long)region.Width * region.Height > RegionCoordinates.MaximumPixels
            || (long)region.X + region.Width > int.MaxValue || (long)region.Y + region.Height > int.MaxValue)
            throw new ArgumentException("固定範囲の座標または寸法が正しくありません。");
    }

    internal static ResolvedCaptureCommand Resolve(CaptureCommandRequest request, AppSettings settings)
    {
        if (request.Region is Rectangle bounds) ValidateRegion(bounds);
        settings = CaptureSettingsSnapshot.Copy(settings);
        settings.Validate();
        settings = settings.EnsurePresets();
        SavedRegion input = request.InputPresetId is null ? settings.SavedRegions.Single(p => p.IsDefault)
            : settings.SavedRegions.SingleOrDefault(p => p.Id == request.InputPresetId)
                ?? throw new ArgumentException("入力プリセットが見つかりません: " + request.InputPresetId);
        CapturePreset output = request.OutputPresetId is null ? settings.CapturePresets.Single(p => p.IsDefault)
            : settings.CapturePresets.SingleOrDefault(p => p.Id == request.OutputPresetId)
                ?? throw new ArgumentException("出力プリセットが見つかりません: " + request.OutputPresetId);
        settings = output.Apply(input.Apply(settings)).ResolveEditors();
        settings.Validate();
        if (settings.DefaultDestination == OutputDestination.None)
            throw new ArgumentException("出力先がすべてOFFです。出力プリセットを確認してください。");
        return new(request, settings, input);
    }

    internal static string Generate(string executable, CaptureCommandRequest request)
    {
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || executable.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || executable.Any(char.IsControl))
            throw new ArgumentException("実行ファイルにはexeの完全パスを指定してください。");
        List<string> args = [];
        if (request.Region is Rectangle bounds)
        {
            ValidateRegion(bounds);
            args.AddRange(["--capture-region", bounds.X.ToString(CultureInfo.InvariantCulture), bounds.Y.ToString(CultureInfo.InvariantCulture),
                bounds.Width.ToString(CultureInfo.InvariantCulture), bounds.Height.ToString(CultureInfo.InvariantCulture)]);
        }
        else args.Add("--capture-select");
        if (request.InputPresetId is not null) args.AddRange(["--input-preset", request.InputPresetId]);
        if (request.OutputPresetId is not null) args.AddRange(["--output-preset", request.OutputPresetId]);
        _ = Parse(args);
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "& " + Quote(executable) + " " + string.Join(" ", args.Select(Quote));
    }
}

internal static class CaptureCommandRunner
{
    internal static async Task<int> ExecuteAsync(ResolvedCaptureCommand command, OutputRouter output,
        Func<Rectangle, AppSettings, Task<(Bitmap Image, CaptureMetadata? Metadata)>>? take = null,
        Func<CaptureOptions, Task<CaptureOutcome>>? select = null, Action<string, bool>? report = null)
    {
        try
        {
            AppSettings settings = command.Settings;
            if (settings.DefaultDestination == OutputDestination.None) throw new ArgumentException("出力先がすべてOFFです。");
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            bool delivered = false;
            if (command.Request.Region is Rectangle bounds)
            {
                CaptureCommandLine.ValidateRegion(bounds);
                var capture = await (take ?? InstantCapture.TakeWithContextAsync)(bounds, settings);
                using var image = capture.Image;
                string result = await InstantCapture.DeliverAsync(image, settings, capturedAt, output,
                    regionName: command.InputPreset.Name, captureBounds: bounds, metadata: capture.Metadata, preserveOutputs: true,
                    onDelivered: () => delivered = true);
                report?.Invoke(result, false);
            }
            else
            {
                using CaptureOutcome outcome = await (select ?? (options => CaptureSession.SelectAsync(options)))(command.SelectionOptions());
                if (outcome.Selection is null) return 2;
                if (outcome.Image is null) throw new InvalidOperationException("撮影画像がありません。");
                settings = (outcome.OutputSettings ?? settings) with { DefaultDestination = outcome.Selection.Destination };
                if (settings.DefaultDestination == OutputDestination.None) throw new ArgumentException("出力先がすべてOFFです。");
                string result = await InstantCapture.DeliverAsync(outcome.Image, settings, capturedAt, output,
                    regionName: command.InputPreset.Name, captureBounds: outcome.Selection.Bounds, metadata: outcome.Metadata,
                    preserveOutputs: true, capturePolygon: outcome.Selection.Polygon, onDelivered: () => delivered = true);
                report?.Invoke(result, false);
            }
            if (!delivered) throw new InvalidOperationException("撮影画像を出力できませんでした。");
            return 0;
        }
        catch (Exception ex)
        {
            report?.Invoke("撮影できませんでした: " + ex.Message, true);
            return 1;
        }
    }
}

internal sealed class CaptureCommandApplicationContext : ApplicationContext
{
    private readonly ResolvedCaptureCommand command;
    private readonly PinnedImageManager pins;
    private readonly OutputRouter output;
    private readonly System.Windows.Forms.Timer start = new() { Interval = 1 };
    private bool finished;

    internal int ExitCode { get; private set; } = 1;

    internal CaptureCommandApplicationContext(ResolvedCaptureCommand command)
    {
        this.command = command;
        pins = new PinnedImageManager(Report);
        pins.Changed += PinsChanged;
        output = new OutputRouter(Path.Combine(AppContext.BaseDirectory, "data", "work"),
            pinImage: pins.Show);
        start.Tick += StartCapture;
        start.Start();
    }

    private static void Report(string message, bool error)
    {
        if (error) Console.Error.WriteLine(message);
        else Console.WriteLine(message);
    }

    private async void StartCapture(object? sender, EventArgs e)
    {
        start.Stop();
        ExitCode = await CaptureCommandRunner.ExecuteAsync(command, output, report: Report);
        finished = true;
        if (pins.Count == 0) ExitThread();
    }

    private void PinsChanged(object? sender, EventArgs e)
    {
        if (finished && pins.Count == 0) ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            start.Dispose();
            pins.Changed -= PinsChanged;
            pins.Dispose();
            output.Dispose();
        }
        base.Dispose(disposing);
    }
}
