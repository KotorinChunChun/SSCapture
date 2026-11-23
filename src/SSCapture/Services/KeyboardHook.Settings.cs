namespace SSCapture.Services;

internal sealed partial class KeyboardHook
{
    private HotkeyBridgeServer? bridge;
    internal int? HotkeyBridgePid => Enabled ? bridge?.ActivePid : null;
    public KeyboardHook(Action onCapture) : this(WrapLegacyCallback(onCapture), new AppSettings
    {
        ScreenshotShortcut1 = "Win+Shift+S", ScreenshotShortcut2 = "",
        RecordingShortcut1 = "", RecordingShortcut2 = "", InstantShortcuts = Enumerable.Repeat("", 10).ToArray()
    }) { }

    public KeyboardHook(Action<CaptureIntent> onCapture, AppSettings settings) : this(onCapture, settings, true) { }

    public KeyboardHook(Action<CaptureIntent> onCapture, AppSettings settings, bool initiallyEnabled,
        Action<int>? onInstantCapture = null, Action<string>? onRegionCapture = null, Action<string>? onPresetCapture = null)
        : this(HotkeyBinding.ReadAssignments(settings), MakeDispatch(onCapture, onInstantCapture, onRegionCapture, onPresetCapture),
            initiallyEnabled)
    {
        bridge = HotkeyBridgeServer.TryStart(this, assignments);
    }

    partial void CancelBridgeLease() => bridge?.CancelLease();
    partial void DisposeBridge() => bridge?.Dispose();

    private static Action<CaptureIntent> WrapLegacyCallback(Action onCapture)
    {
        ArgumentNullException.ThrowIfNull(onCapture);
        return _ => onCapture();
    }

    private static Action<HotkeyDecision> MakeDispatch(Action<CaptureIntent> onCapture, Action<int>? onInstantCapture,
        Action<string>? onRegionCapture, Action<string>? onPresetCapture)
    {
        ArgumentNullException.ThrowIfNull(onCapture);
        return decision =>
        {
            if (decision.PresetKey is { } key) onPresetCapture?.Invoke(key);
            else if (decision.RegionId is { } id) onRegionCapture?.Invoke(id);
            else if (decision.DisplayNumber > 0) onInstantCapture?.Invoke(decision.DisplayNumber);
            else onCapture(decision.Intent);
        };
    }
}
