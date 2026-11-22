namespace SSCapture.Tests;

internal static partial class Program
{
    private static int passed;
    private static int failed;
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("--context-stall")) { Console.WriteLine("ready"); Console.In.ReadLine(); Thread.Sleep(30000); return 0; }
        if (args.FirstOrDefault() == "--editor-received") { File.WriteAllText(Path.Combine(args[3], "editor-" + args[1] + ".json"), System.Text.Json.JsonSerializer.Serialize(new { application = args[1], image = args[2] })); return 0; }
        if (args.FirstOrDefault() == "--handoff-test-worker") return HoldTestHandoff(args[1]);
        if (args is ["--browser-job-tests", var jobs]) return RunBrowserJobTests(jobs);
        if (args is ["--browser-job-probe", var role, var fixture]) return RunBrowserJobProbe(role, fixture);
        if (args is ["--standard-reset-tests"])
        {
            StandardRegistrationResetTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.FirstOrDefault() == "--browser-control-smoke" && args is not ["--browser-control-smoke", "--interactive", _, _])
        {
            Console.Error.WriteLine("専用デスクトップで --browser-control-smoke --interactive <ブラウザーexe> <検証フォルダー> を指定してください。");
            return 2;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.FirstOrDefault() == "--notification-drag-preview") return NotificationDragPreview(args[1]);
        if (args.FirstOrDefault() == "--store-editor-open") return OpenStoreEditorSmoke(args);
        if (args.FirstOrDefault() == "--recording-desktop-smoke") return RunRecordingDesktopSmoke(args);
        if (args.SequenceEqual(["--notification-tests"]))
        {
            NotificationUpgradeTests(); NotificationActionTests(); NotificationPresentationTests(); NotificationDragTests(); FeedbackTests(); CaptureTargetTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--optics-tests"]))
        {
            CaptureOpticsTests(); SnapTests(); RecipeTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--replay-tests"]))
        {
            RecipeTests(); SnapshotRefactorTests(); RepeatTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--recipe-tests"]))
        {
            RecipeTests(); SnapshotRefactorTests(); RepeatTests(); InstantTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--snap-tests"]))
        {
            SnapTests(); SelectionGuideTests(); SelectionUpgradeTests(); SelectionMonitorTests(); SelectionInteractionTests(); FixedCaptureTests(); OverlayWindowTests(); PreparationTests(); CaptureCompositeTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--menu-tests"]))
        {
            CaptureMenuEditingTests(); SnapTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--shortcut-tests"]))
        {
            PresetShortcutTests(includeSettingsForm: false);
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--preset-tests"]))
        {
            PresetShortcutTests(); PresetConsolidationTests(); PresetUpgradeTests(); AutoSaveBindingTests(); UiTests(); RegionTests(); RegionUiTests(); CapturePresetTests(); ExternalApplicationTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.FirstOrDefault() == "--external-editor-launch")
        {
            try
            {
                var info = Services.OutputRouter.EditorStartInfo(args[1], Path.GetFullPath(args[2]));
                using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("起動できません。");
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { info.FileName, Arguments = info.ArgumentList.ToArray(), process.Id }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--clipboard-color-smoke")) return ClipboardColorSmoke(args[1], args[2]);
        if (args.Contains("--preparation-smoke")) return PreparationSmoke(args[1]);
        if (args.Contains("--context-smoke")) return ContextSmoke(args[1]);
        if (args.Contains("--upgrade-ui-preview")) return UpgradeUiPreview(args[1]);
        if (args.Contains("--capture-menu-preview")) return CaptureMenuPreview(args[1]);
        if (args.FirstOrDefault() == "--pinned-preview") return PinnedImagePreview(args[1]);
        if (args.FirstOrDefault() == "--notification-preview") return NotificationPreview(args[1], false);
        if (args.FirstOrDefault() == "--notification-underlay") return NotificationPreview(args[1], true);
        if (args.Contains("--padding-ui-preview")) return PaddingUiPreview(args[1]);
        if (args.Contains("--selection-guide-preview")) return SelectionGuidePreview(args[1]);
        if (args.Contains("--overlay-placement-smoke")) return RunOverlayPlacementSmoke();
        if (args.Contains("--presentation-smoke")) return RunPresentationSmoke(useNativeInput: !args.Contains("--no-native-input"));
        if (args.Contains("--control-drag-smoke")) return RunControlDragSmoke();
        if (args.Contains("--control-capture-smoke")) return RunControlCaptureSmoke();
        if (args.Contains("--control-quick-output-smoke")) return RunQuickControlOutputSmoke();
        if (args is ["--browser-control-smoke", "--interactive", var browser, var scratch]) return RunBrowserControlSmoke(browser, scratch);
        if (args.Length == 2 && args[0] == "--timer-native-smoke") return RunTimerNativeSmoke(args[1]);
        if (args.Contains("--selection-monitor-smoke")) return RunSelectionMonitorSmoke();
        if (args.Contains("--window-hit-smoke")) return RunWindowHitSmoke();
        if (args.Contains("--selection-input-smoke")) return RunSelectionInputSmoke();
        if (args.Contains("--settings-win-smoke")) return RunSettingsWindowsKeySmoke();
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
        if (args.SequenceEqual(["--pinned-tests"]))
        { PinnedImageTests(); Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
        if (args.SequenceEqual(["--command-tests"]))
        { CaptureCommandTests(); CaptureTimerTests(); CaptureStartDelayTests(); Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
        if (args.SequenceEqual(["--output-tests"]))
        {
            OutputTests(); OutputConditionTests(); ScreenshotPaddingTests(); ScreenshotColorTests();
            ExternalApplicationTests(); MetadataStorageTests(); SaveCancellationTests(); HistoryUiTests();
            DeliveryTests(); DeliveryWaitTests(); NamingTests(); OutputRetryTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--recording-tests"]))
        {
            RecordingTests(); RecordingSessionTests(); AudioToggleTests();
            Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
        if (args.SequenceEqual(["--startup-tests"]))
        { StartupTests(); SettingsTransferTests(); Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
        if (args is ["--scroll-ui-smoke"]) return ScrollDesktopSmoke();
        if (args.SequenceEqual(["--scroll-tests"]))
        { ScrollTests(); Console.WriteLine($"RESULT: {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1; }
        Run("6つの撮影モード", () => Equal(6, Enum.GetValues<CaptureMode>().Length));
        ScrollTests();
        PinnedImageTests();
        CaptureCommandTests();
        CaptureTimerTests();
        CaptureStartDelayTests();
        CaptureTests();
        CaptureMenuEditingTests();
        PresetConsolidationTests();
        PresetUpgradeTests();
        StandardRegistrationResetTests();
        PresetShortcutTests();
        AutoSaveBindingTests();
        RecipeTests();
        SnapshotRefactorTests();
        OverlayWindowTests();
        CaptureCompositeTests();
        SelectionMonitorTests();
        ServiceTests();
        UiTests();
        StartupTests();
        SettingsTransferTests();
        RecordingTests();
        RecordingSessionTests();
        AudioToggleTests();
        OutputTests();
        ScreenshotPaddingTests();
        ScreenshotColorTests();
        PreparationTests();
        NotificationUpgradeTests();
        NotificationActionTests();
        NotificationDragTests();
        NotificationPresentationTests();
        OutputConditionTests();
        ExternalApplicationTests();
        MetadataContextTests();
        MetadataStorageTests();
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
        CaptureOpticsTests();
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
