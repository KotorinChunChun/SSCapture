using System.Globalization;
using SSCapture.Capture;
using SSCapture.UI;

namespace SSCapture.Services;

internal sealed record CaptureCommandRequest(Rectangle? Region = null, string? InputPresetId = null, string? OutputPresetId = null)
{
    internal CaptureSchedule? Schedule { get; init; }
    internal bool UsePresetMode { get; init; }
    internal WindowCaptureCommand? Window { get; init; }
}

internal sealed record ResolvedCaptureCommand(CaptureCommandRequest Request, AppSettings Settings, SavedRegion InputPreset, CapturePreset? OutputPreset = null)
{
    internal Rectangle? DirectBounds(Func<IReadOnlyList<DisplayLayout>>? readDisplays = null) => Request.Region ??
        (Request.UsePresetMode && InputPreset.UseFixedCoordinates
            ? RegionCoordinates.Resolve(InputPreset, (readDisplays ?? RegionCoordinates.ReadDisplays)()) : null);

    internal CaptureOptions SelectionOptions()
    {
        // 選択方式では登録座標や全画面設定を即時撮影へ読み替えない。
        SavedRegion input = Request.UsePresetMode ? InputPreset : InputPreset with
        {
            UseFixedCoordinates = false,
            Mode = InputPreset.UseFixedCoordinates || InputPreset.Mode == CaptureMode.FullScreen ? CaptureMode.Rectangle : InputPreset.Mode
        };
        return new(input.Mode, CaptureIntent.Screenshot, Settings.DefaultDestination, Settings.FreezeFrame,
            IncludeCursor: Settings.IncludeCursor, FixedSize: new(Settings.FixedWidth, Settings.FixedHeight),
            FixedAnchor: Settings.FixedCaptureAnchor, LimitToStartingMonitor: Settings.LimitToStartingMonitor,
            Grid20: Settings.Grid20, ShowSelectionGuide: Settings.ShowSelectionGuide, OutputSettings: Settings,
            DimCaptureBackground: Settings.DimCaptureBackground, InitialInputPreset: input, InitialOutputPreset: OutputPreset)
        { EnableStartDelay = Request.Schedule is null };
    }
}

internal static class CaptureCommandLine
{
    internal static bool Handles(IReadOnlyList<string> args) => args.Any(arg =>
        arg.StartsWith("--capture-", StringComparison.Ordinal) && arg != "--capture-context-worker"
        || arg is "--input-preset" or "--output-preset" or "--hwnd" or "--process" or "--title" or "--client-area"
        || CaptureTimerArguments.Handles(arg));

    internal static CaptureCommandRequest Parse(IReadOnlyList<string> args)
    {
        bool mode = false;
        bool usePresetMode = false;
        bool windowMode = false, clientAreaOnly = false;
        nint? handle = null;
        string? process = null, title = null;
        Rectangle? region = null;
        string? input = null, output = null;
        Dictionary<string, string> scheduleOptions = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index++)
        {
            string option = args[index];
            switch (option)
            {
                case "--capture-preset":
                case "--capture-select":
                case "--capture-window":
                    if (mode) throw new ArgumentException("撮影方式は1つだけ指定してください。");
                    mode = true;
                    usePresetMode = option == "--capture-preset";
                    windowMode = option == "--capture-window";
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
                case "--hwnd":
                    if (handle.HasValue) throw new ArgumentException("--hwnd が重複しています。");
                    handle = WindowCaptureCommand.ParseHandle(ReadWindowValue(option, args, ref index));
                    break;
                case "--process":
                    if (process is not null) throw new ArgumentException("--process が重複しています。");
                    process = ReadWindowValue(option, args, ref index);
                    break;
                case "--title":
                    if (title is not null) throw new ArgumentException("--title が重複しています。");
                    title = ReadWindowValue(option, args, ref index);
                    break;
                case "--client-area":
                    if (clientAreaOnly) throw new ArgumentException("--client-area が重複しています。");
                    clientAreaOnly = true;
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
                case var timer when CaptureTimerArguments.Handles(timer):
                    CaptureTimerArguments.Read(scheduleOptions, option, args, ref index);
                    break;
                default: throw new ArgumentException("不明な撮影引数です: " + option);
            }
        }
        if (!mode) throw new ArgumentException("--capture-select、--capture-region、--capture-preset、--capture-window のいずれかを指定してください。");
        if (!windowMode && (handle.HasValue || process is not null || title is not null || clientAreaOnly))
            throw new ArgumentException("ウィンドウ指定の引数は --capture-window と一緒に使用してください。");
        WindowCaptureCommand? window = windowMode ? new(handle, process, title, clientAreaOnly) : null;
        window?.Validate();
        return new(region, input, output) { UsePresetMode = usePresetMode, Schedule = CaptureTimerArguments.Build(scheduleOptions), Window = window };
    }

    private static string ReadWindowValue(string option, IReadOnlyList<string> args, ref int index)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal)
            || args[index].Any(char.IsControl))
            throw new ArgumentException(option + " の値を指定してください。");
        return args[index];
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
        ValidateWindowMode(request);
        if (request.Region is Rectangle bounds)
        {
            if (request.UsePresetMode) throw new ArgumentException("撮影方式は1つだけ指定してください。");
            ValidateRegion(bounds);
        }
        settings = CaptureSettingsSnapshot.Copy(settings);
        settings.Validate();
        settings = settings.EnsurePresets();
        var preset = ResolvedPresetCapture.Resolve(settings, request.InputPresetId, request.OutputPresetId);
        if (request.Schedule is not null && request.UsePresetMode && preset.Input.Mode == CaptureMode.Scroll)
            throw new ArgumentException("スクロール撮影は手動操作が必要です。予約撮影では固定座標または通常の範囲選択を指定してください。");
        var output = request.OutputPresetId is null && request.Schedule is CaptureSchedule.Periodic
            ? CaptureTimerOutput.InitialPeriodic(settings) : preset.Output;
        settings = output == preset.Output ? preset.Settings : output.Apply(preset.Input.Apply(settings)).ResolveEditors();
        settings.Validate();
        if (settings.DefaultDestination == OutputDestination.None)
            throw new ArgumentException("出力先がすべてOFFです。出力プリセットを確認してください。");
        return new(request, settings, preset.Input, output);
    }

    internal static string Generate(string executable, CaptureCommandRequest request)
    {
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || executable.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || executable.Any(char.IsControl))
            throw new ArgumentException("実行ファイルにはexeの完全パスを指定してください。");
        List<string> args = [];
        ValidateWindowMode(request);
        if (request.Region is Rectangle bounds)
        {
            if (request.UsePresetMode) throw new ArgumentException("撮影方式は1つだけ指定してください。");
            ValidateRegion(bounds);
            args.AddRange(["--capture-region", bounds.X.ToString(CultureInfo.InvariantCulture), bounds.Y.ToString(CultureInfo.InvariantCulture),
                bounds.Width.ToString(CultureInfo.InvariantCulture), bounds.Height.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (request.Window is { } window)
        {
            args.Add("--capture-window");
            if (window.Handle is nint handle) args.AddRange(["--hwnd", "0x" + unchecked((nuint)handle).ToString("X", CultureInfo.InvariantCulture)]);
            if (window.ProcessName is not null) args.AddRange(["--process", window.ProcessName]);
            if (window.Title is not null) args.AddRange(["--title", window.Title]);
            if (window.ClientAreaOnly) args.Add("--client-area");
        }
        else args.Add(request.UsePresetMode ? "--capture-preset" : "--capture-select");
        if (request.InputPresetId is not null) args.AddRange(["--input-preset", request.InputPresetId]);
        if (request.OutputPresetId is not null) args.AddRange(["--output-preset", request.OutputPresetId]);
        CaptureTimerArguments.Append(args, request.Schedule);
        _ = Parse(args);
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return "& " + Quote(executable) + " " + string.Join(" ", args.Select(Quote));
    }

    private static void ValidateWindowMode(CaptureCommandRequest request)
    {
        if (request.Window is not { } window) return;
        if (request.Region.HasValue || request.UsePresetMode) throw new ArgumentException("撮影方式は1つだけ指定してください。");
        window.Validate();
    }
}

internal static partial class CaptureCommandRunner
{
    internal static async Task<int> ExecuteAsync(ResolvedCaptureCommand command, OutputRouter output,
        Func<Rectangle, AppSettings, Task<(Bitmap Image, CaptureMetadata? Metadata)>>? take = null,
        Func<CaptureOptions, Task<CaptureOutcome>>? select = null, Action<string, bool>? report = null,
        Func<IReadOnlyList<DisplayLayout>>? readPresetDisplays = null, Func<TimeSpan, Task>? wait = null,
        Func<WindowCaptureCommand, CaptureWindow>? resolveWindow = null,
        Func<CaptureWindow, AppSettings, bool, bool, Task<WindowReplayFrame>>? takeWindow = null)
    {
        try
        {
            if (command.Request.Schedule is not null)
                return await ExecuteTimerAsync(command, output, take, select, report, resolveWindow, takeWindow);
            AppSettings settings = command.Settings;
            if (settings.DefaultDestination == OutputDestination.None) throw new ArgumentException("出力先がすべてOFFです。");
            DateTimeOffset capturedAt = DateTimeOffset.Now;
            bool delivered = false;
            if (command.Request.Window is not null)
            {
                // 遅延前に対象を確定し、閉じた後に別のウィンドウを探索し直さない。
                var shot = CaptureTimerShot.Window(command, resolveWindow, takeWindow);
                await CaptureDelay.WaitAsync(command.InputPreset.StartDelay, wait);
                string result = await shot.DeliverAsync(output, DateTimeOffset.Now);
                report?.Invoke(result, false);
                return 0;
            }
            if (command.Request.Region.HasValue || command.Request.UsePresetMode && command.InputPreset.UseFixedCoordinates)
            {
                if (command.InputPreset.StartDelay != CaptureStartDelay.None)
                {
                    await CaptureDelay.WaitAsync(command.InputPreset.StartDelay, wait);
                    capturedAt = DateTimeOffset.Now;
                }
                var bounds = command.DirectBounds(readPresetDisplays)!.Value;
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
                var options = command.SelectionOptions();
                using CaptureOutcome outcome = select is null ? await CaptureSession.SelectAsync(options)
                    : await CaptureSession.SelectWithDelayAsync(options, select, wait);
                capturedAt = outcome.CaptureStartedAt ?? capturedAt;
                if (outcome.Selection is null) return 2;
                if (outcome.Image is null) throw new InvalidOperationException("撮影画像がありません。");
                settings = (outcome.OutputSettings ?? settings) with { DefaultDestination = outcome.Selection.Destination };
                if (settings.DefaultDestination == OutputDestination.None) throw new ArgumentException("出力先がすべてOFFです。");
                string result = await InstantCapture.DeliverAsync(outcome.Image, settings, capturedAt, output,
                    regionName: outcome.Selection.RegionName ?? command.InputPreset.Name, captureBounds: outcome.Selection.Bounds, metadata: outcome.Metadata,
                    preserveOutputs: true, capturePolygon: outcome.Selection.Polygon, onDelivered: () => delivered = true,
                    windowTitle: outcome.Selection.WindowTitle, replaySource: outcome.Selection.ReplaySource);
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
