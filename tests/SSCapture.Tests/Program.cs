namespace SSCapture.Tests;

internal static partial class Program
{
    private static int passed;
    private static int failed;
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.Contains("--padding-ui-preview")) return PaddingUiPreview(args[1]);
        if (args.Contains("--selection-guide-preview")) return SelectionGuidePreview(args[1]);
        if (args.Contains("--overlay-placement-smoke")) return RunOverlayPlacementSmoke();
        if (args.Contains("--selection-monitor-smoke")) return RunSelectionMonitorSmoke();
        if (args.Contains("--window-hit-smoke")) return RunWindowHitSmoke();
        if (args.Contains("--selection-input-smoke")) return RunSelectionInputSmoke();
        if (args.Contains("--paint-launch-smoke")) return RunPaintLaunchSmoke(args[1]);
        if (args.Contains("--capture-target-toast-smoke")) return RunCaptureTargetToastSmoke();
        if (args.Contains("--ocr-smoke")) return RunOcrSmoke();
        if (args.Contains("--ocr-ui-preview")) return OcrPreview();
        if (args.Contains("--display-routing-smoke")) return DisplayRoutingSmoke();
        if (args.Contains("--workflow-ui-preview")) return WorkflowPreview(args[1]);
        if (args.Contains("--interactive-smoke")) return RunInteractiveSmoke();
        if (args.Contains("--feedback-native-smoke")) return RunFeedbackNativeSmoke();
        if (args.Contains("--feedback-exclusion-preview")) return RunFeedbackNativeSmoke(preview: true);
        if (args.Contains("--explorer-selection-smoke")) return RunExplorerSelectionSmoke(args[1]);
        if (args.Contains("--media-smoke"))
        {
            try
            {
                if (args.Length != 4) throw new ArgumentException("--media-smoke ffmpeg ffprobe workRoot");
                Console.WriteLine(Recording.RecordingMediaSmoke.RunAsync(args[1], args[2], args[3]).GetAwaiter().GetResult());
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        Run("5つの撮影モード", () => Equal(5, Enum.GetValues<CaptureMode>().Length));
        CaptureTests();
        OverlayWindowTests();
        SelectionMonitorTests();
        ServiceTests();
        UiTests();
        RecordingTests();
        RecordingSessionTests();
        AudioToggleTests();
        OutputTests();
        ScreenshotPaddingTests();
        ScreenshotColorTests();
        SaveCancellationTests();
        DeliveryWaitTests();
        StorageRefactorTests();
        EditorTests();
        NamingTests();
        OcrTests();
        InstantTests();
        RepeatTests();
        RegionTests();
        CursorTests();
        RegionUiTests();
        HistoryUiTests();
        FixedCaptureTests();
        DeliveryTests();
        FeedbackTests();
        CaptureTargetTests();
        SnapTests();
        SelectionInteractionTests();
        SelectionUpgradeTests();
        CapturePresetTests();
        OutputRetryTests();
        SelectionGuideTests();
        StatusTests();
        TrayOperationPolicyTests();
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
    static partial void CaptureTests();
    static partial void ServiceTests();
    static partial void UiTests();
    static partial void RecordingTests();
    static partial void RecordingSessionTests();
    static partial void AudioToggleTests();
    static partial void OutputTests();
    static partial void ScreenshotPaddingTests();
    static partial void SaveCancellationTests();
    static partial void DeliveryWaitTests();
    static partial void StorageRefactorTests();
    static partial void EditorTests();
    static partial void NamingTests();
    static partial void OcrTests();
    static partial void InstantTests();
    static partial void RepeatTests();
    static partial void RegionTests();
    static partial void CursorTests();
    static partial void RegionUiTests();
    static partial void HistoryUiTests();
    static partial void FixedCaptureTests();
    static partial void DeliveryTests();
    static partial void FeedbackTests();
    static partial void SnapTests();
    static partial void StatusTests();
    internal static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }
    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; actual {actual}");
    }
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
