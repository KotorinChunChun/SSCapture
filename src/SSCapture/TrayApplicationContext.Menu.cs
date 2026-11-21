using SSCapture.Capture;

namespace SSCapture;

internal sealed partial class TrayApplicationContext
{
    private void InitializeMonitorMenu()
    {
        var monitors = new ToolStripMenuItem("モニターを即時撮影") { Name = "InstantMonitorCapture" };
        menu.Items.Add(monitors);
        menu.Opening += (_, _) =>
        {
            foreach (ToolStripItem item in monitors.DropDownItems.Cast<ToolStripItem>().ToArray())
                item.Dispose();

            var displays = InstantCapture.ReadDisplays();
            for (int index = 0; index < displays.Count; index++)
            {
                DisplayTarget display = displays[index];
                int number = index + 1;
                monitors.DropDownItems.Add(new ToolStripMenuItem(
                    $"画面{number}{(display.IsPrimary ? "（主）" : "")}（{display.Bounds.Width}×{display.Bounds.Height}）",
                    null, (_, _) => Post(() => _ = TakeInstantAsync(number, display))));
            }

            monitors.Enabled = displays.Count != 0 && !busy && pendingRecording is null && !OperationPolicy.Naming;
        };
    }

    private void AddPreviousCaptureMenu()
    {
        var previous = new ToolStripMenuItem("前回の撮影結果を表示", null, (_, _) =>
        {
            if (!OperationPolicy.MustDeferFeedback) feedback.ShowPreviousScreenshot(settings);
        }) { Name = "ShowPreviousCapture" };
        menu.Items.Add(previous);
        menu.Opening += (_, _) => previous.Enabled =
            !OperationPolicy.MustDeferFeedback && feedback.CanShowPreviousScreenshot(settings);
    }
}
