using SSCapture.Capture;

namespace SSCapture.Services;

internal static partial class CaptureCommandRunner
{
    private static async Task<int> ExecuteTimerAsync(ResolvedCaptureCommand command, OutputRouter output,
        Func<Rectangle, AppSettings, Task<(Bitmap Image, CaptureMetadata? Metadata)>>? take,
        Func<CaptureOptions, Task<CaptureOutcome>>? select, Action<string, bool>? report)
    {
        CaptureTimerShot? shot;
        if (command.Request.Region is Rectangle bounds)
            shot = CaptureTimerShot.Fixed(bounds, command, take);
        else
            shot = await SelectTimerTargetAsync(command, select);
        if (shot is null) return 2;

        using var timer = new CaptureTimerPump(command.Request.Schedule!, async capturedAt =>
        {
            string result = await shot.DeliverAsync(output, capturedAt);
            report?.Invoke(result, false);
        });
        await timer.RunAsync();
        return 0;
    }

    internal static async Task<CaptureTimerShot?> SelectTimerTargetAsync(ResolvedCaptureCommand command,
        Func<CaptureOptions, Task<CaptureOutcome>>? select = null, CaptureMode? mode = null)
    {
        CaptureOptions options = command.SelectionOptions() with { SelectionOnly = true };
        if (mode is { } selectionMode)
            options = options with { Mode = selectionMode, InitialInputPreset = options.InitialInputPreset! with { Mode = selectionMode } };
        using var outcome = await (select ?? (pickerOptions => CaptureSession.SelectAsync(pickerOptions)))(options);
        if (outcome.Selection is not { } selection) return null;
        var chosen = outcome.OutputSettings ?? command.Settings;
        AppSettings settings = command.Settings with { IncludeCursor = chosen.IncludeCursor, ScreenshotPaddingPixels = chosen.ScreenshotPaddingPixels };
        return CaptureTimerShot.FromSelection(selection, settings, command.InputPreset.Name);
    }
}
