using System.ComponentModel;
using System.Diagnostics;

namespace SSCapture.Capture;

internal sealed partial class SessionController
{
    private readonly Func<IReadOnlyList<DisplayTarget>>? readGuideDisplays;
    private IReadOnlyDictionary<Rectangle, string>? guideMonitorNames;
    private readonly Dictionary<uint, string> guideProcessNames = [];

    internal Rectangle? PointerHintCoordinates
    {
        get
        {
            if (!OutputSettings.ShowPointerGuide || PointerLabelsSuppressed) return null;
            if (SelectingControl) return controlCandidate?.Bounds;
            if (pressedButton == MouseButtons.Middle || HighlightWindow) return null;
            return (Mode == CaptureMode.FixedRegion ? FixedSelectionValid : gesture.HasDragged)
                && CaptureGeometry.IsUsable(Selection) ? Selection : null;
        }
    }

    private IEnumerable<string> CaptureTargetHints()
    {
        if (SelectingControl)
        {
            if (controlCandidate is { } control)
            {
                yield return DescribeWindow(control.Window);
                yield return DescribeBounds(control.Bounds);
            }
            yield break;
        }

        if (pressedButton == MouseButtons.Middle)
        {
            var monitors = FreeMiddleDrag && targetDrag is { Monitors.Count: > 0 }
                ? (IEnumerable<Rectangle>)targetDrag.Monitors : [gesture.MonitorAt(Pointer)];
            foreach (Rectangle monitor in monitors)
                if (CaptureGeometry.IsUsable(monitor)) yield return DescribeMonitor(monitor);
            yield break;
        }

        if (HasWindowDrag && targetDrag!.IncludedWindows.Count > 0)
        {
            foreach (CaptureWindow window in targetDrag.IncludedWindows) yield return DescribeWindow(window);
            yield break;
        }

        if (!TwoPointSelection && (HighlightWindow || HasPointerPress && !gesture.HasDragged
            && Mode is CaptureMode.Rectangle or CaptureMode.Freeform))
        {
            Point point = gesture.WindowTargetPoint;
            if (!CaptureGeometry.IsUsable(gesture.WindowAt(point))) yield break;
            if (findWindow?.Invoke(point) is { } target)
            {
                yield return DescribeWindow(target);
                if (UsesWindowSource && windowPreview is { } preview && preview.Target == target)
                    foreach (CaptureWindow window in preview.Windows)
                        if (window.Handle != target.Handle) yield return DescribeWindow(window);
            }
            else if (readTitle?.Invoke(point) is { Length: > 0 } title) yield return title;
            yield break;
        }

        if (PointerHintCoordinates is Rectangle bounds) yield return DescribeBounds(bounds);
    }

    private string DescribeMonitor(Rectangle monitor)
    {
        guideMonitorNames ??= (readGuideDisplays?.Invoke() ??
            Snapshot.Monitors.Select(bounds => new DisplayTarget("", bounds)).ToArray())
            .Select((display, index) =>
            {
                string? name = display.MonitorName ?? display.DeviceName;
                string label = $"デスクトップ {index + 1}";
                return (display.Bounds, Label: string.IsNullOrWhiteSpace(name) ? label : $"{label}（{name}）");
            })
            .DistinctBy(display => display.Bounds)
            .ToDictionary(display => display.Bounds, display => display.Label);
        return guideMonitorNames.TryGetValue(monitor, out string? description) ? description : DescribeBounds(monitor);
    }

    private string DescribeWindow(CaptureWindow window)
    {
        if (!guideProcessNames.TryGetValue(window.ProcessId, out string? name))
        {
            name = $"PID {window.ProcessId}";
            if (window.ProcessId is > 0 and <= int.MaxValue)
            {
                try
                {
                    using var process = Process.GetProcessById((int)window.ProcessId);
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
            guideProcessNames.Add(window.ProcessId, name);
        }
        string title = string.IsNullOrWhiteSpace(window.Title) ? "タイトルなし" : window.Title;
        return $"{title}（{name}）";
    }

    private static string DescribeBounds(Rectangle bounds) =>
        $"座標（{bounds.X}, {bounds.Y}　{bounds.Width} × {bounds.Height}）";
}
