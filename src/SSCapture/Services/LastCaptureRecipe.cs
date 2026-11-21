using System.ComponentModel;
using System.Diagnostics;
using SSCapture.Capture;

namespace SSCapture.Services;

// 画像履歴とは独立した、成功した静止画の対象と入出力条件の記録。
internal sealed class LastCaptureRecipe
{
    private readonly AppSettings settings;
    private readonly SavedRegion? region;
    private readonly CaptureWindowIdentity[]? windows;
    private readonly string[]? monitors;
    internal long Sequence { get; }
    internal Rectangle Bounds { get; }
    internal bool TracksWindows => windows is not null;
    internal bool TracksMonitors => monitors is not null;
    internal bool HasTrackedTarget => TracksWindows || TracksMonitors;
    internal bool UseIsolatedWindow { get; }
    internal bool TracksControl { get; }
    internal ControlIdentity? ControlIdentity { get; }
    internal bool IncludeOwnedWindows { get; }
    internal bool IsSingleWindowSelection { get; }
    internal IReadOnlyList<Point>? Polygon { get; }
    internal AppSettings Settings => CaptureSettingsSnapshot.Copy(settings);

    private LastCaptureRecipe(long sequence, Rectangle bounds, SavedRegion? region, IReadOnlyList<Point>? polygon, AppSettings settings,
        CaptureReplaySource? source, IReadOnlyList<DisplayLayout> displays, Func<uint, long?> processStart)
    {
        Sequence = sequence;
        this.region = region;
        Bounds = bounds;
        if (source is CaptureReplaySource.Windows selected)
        {
            windows = selected.Targets.Select(window => new CaptureWindowIdentity(window, processStart(window.ProcessId))).ToArray();
            if (windows.Length == 0) throw new InvalidOperationException("再撮影するウィンドウが選ばれていません。");
            UseIsolatedWindow = selected.Isolated;
            IncludeOwnedWindows = selected.IncludeOwned;
            IsSingleWindowSelection = selected.IsSingleWindowSelection;
        }
        if (source is CaptureReplaySource.Monitors selectedMonitors)
        {
            monitors = selectedMonitors.Bounds.Select(member => FindDisplay(displays, display => display.Bounds == member).Identity).ToArray();
            if (monitors.Length == 0) throw new InvalidOperationException("再撮影するモニターが選ばれていません。");
        }
        if (source is CaptureReplaySource.Control control)
        {
            windows = [new CaptureWindowIdentity(control.Window, processStart(control.Window.ProcessId))];
            UseIsolatedWindow = control.Isolated;
            TracksControl = true;
            ControlIdentity = control.Identity;
        }
        Polygon = polygon is null ? null : Array.AsReadOnly(polygon.ToArray());
        this.settings = CaptureSettingsSnapshot.Copy(settings);
    }

    internal static LastCaptureRecipe Create(long sequence, Rectangle bounds, IReadOnlyList<Point>? polygon, AppSettings settings,
        IReadOnlyList<DisplayLayout> displays, CaptureReplaySource? source = null, Func<uint, long?>? processStart = null) =>
        new(sequence, bounds, source is null ? RegionCoordinates.FromBounds("直前の撮影", bounds, displays) : null,
            polygon, settings, source, displays, processStart ?? ReadProcessStart);

    internal Rectangle Resolve(IReadOnlyList<DisplayLayout> displays) => region is null
        ? throw new InvalidOperationException("現在のウィンドウまたはモニターを指定してください。")
        : RegionCoordinates.Resolve(region, displays);

    internal AppSettings SettingsForReplay(AppSettings current) => CaptureSettingsSnapshot.ForReplay(settings, current);

    internal void ValidateApplications(AppSettings current)
    {
        if (!settings.DefaultDestination.HasFlag(OutputDestination.ExternalApp)) return;
        string id = settings.SelectedImageEditorId;
        var original = settings.ImageEditors.SingleOrDefault(a => a.Id == id);
        var now = current.ImageEditors.SingleOrDefault(a => a.Id == id);
        if (original is null || now != original) throw new InvalidOperationException("直前の外部アプリが削除・変更されています。通常撮影で設定を確認してください。");
    }
    internal IReadOnlyList<Point>? PolygonFor(Rectangle bounds) => Polygon?.Select(p => new Point(checked(p.X + bounds.X - Bounds.X), checked(p.Y + bounds.Y - Bounds.Y))).ToArray();
    internal IReadOnlyList<Rectangle>? RegionsFor(IReadOnlyList<DisplayLayout> displays) =>
        monitors?.Select(id => FindDisplay(displays, display => StringComparer.OrdinalIgnoreCase.Equals(display.Identity, id)).Bounds).ToArray();

    internal CaptureWindow[] ResolveWindows(IReadOnlyList<CaptureWindow> current, Func<uint, long?>? processStart = null)
    {
        if (windows is null) throw new InvalidOperationException("再撮影するウィンドウが記録されていません。");
        var starts = windows.Select(window => window.Original.ProcessId).Distinct()
            .ToDictionary(pid => pid, processStart ?? ReadProcessStart);
        var resolved = windows.Select(window => window.Resolve(current, starts[window.Original.ProcessId])).ToArray();
        if (resolved.DistinctBy(window => window.Handle).Count() != resolved.Length)
            throw new InvalidOperationException("再撮影するウィンドウを区別できません。選び直してください。");
        return resolved;
    }

    private static DisplayLayout FindDisplay(IReadOnlyList<DisplayLayout> displays, Func<DisplayLayout, bool> match)
    {
        var found = displays.Where(match).Take(2).ToArray();
        return found.Length == 1 ? found[0]
            : throw new InvalidOperationException("撮影したモニターを識別できません。接続を確認して選び直してください。");
    }

    private static long? ReadProcessStart(uint pid)
    {
        if (pid is 0 or > int.MaxValue) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record CaptureWindowIdentity(CaptureWindow Original, long? ProcessStart)
    {
        internal CaptureWindow Resolve(IReadOnlyList<CaptureWindow> current, long? processStart)
        {
            if (ProcessStart is null || ProcessStart == processStart)
            {
                var candidates = current.Where(window => window.ProcessId == Original.ProcessId
                    && window.ClassName == Original.ClassName).ToArray();
                var sameHandle = candidates.FirstOrDefault(window => window.Handle == Original.Handle && window.ThreadId == Original.ThreadId);
                if (sameHandle is not null) return sameHandle;
                if (ProcessStart is not null && Original.ClassName.Length != 0 && Original.Title.Length != 0)
                {
                    var matching = candidates.Where(window => window.Title == Original.Title).Take(2).ToArray();
                    if (matching.Length == 1) return matching[0];
                }
            }
            throw new InvalidOperationException("再撮影するウィンドウが見つからないか、区別できません。選び直してください。");
        }
    }

    internal Bitmap Shape(Bitmap image) => Polygon is null ? (Bitmap)image.Clone() :
        CaptureGeometry.Freeform(image, Bounds, Polygon) ?? throw new InvalidOperationException("直前の自由形状を再現できません。");
}
