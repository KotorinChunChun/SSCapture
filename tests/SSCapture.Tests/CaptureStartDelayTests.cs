using System.Text.Json;
using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void CaptureStartDelayTests()
    {
        Run("撮影開始タイマーのない旧プリセットと新規の既定は即時で指定秒数を保存する", () =>
        {
            var old = JsonSerializer.Deserialize<SavedRegion>("{}")!;
            Equal(CaptureStartDelay.None, old.StartDelay);
            var settings = new AppSettings().EnsurePresets();
            Check(settings.SavedRegions.All(preset => preset.StartDelay == CaptureStartDelay.None), "初期プリセットに待ち時間が入った");
            var preset = settings.SavedRegions[0] with { StartDelay = CaptureStartDelay.FiveSeconds };
            string json = JsonSerializer.Serialize(preset, SettingsStore.Options);
            Equal(preset.StartDelay, JsonSerializer.Deserialize<SavedRegion>(json, SettingsStore.Options)!.StartDelay);
            CaptureCommandReject(() => (preset with { StartDelay = (CaptureStartDelay)4 }).Validate());
        });

        Run("撮影開始と右クリックタイマーのどちらも待機を完了してから選択を始める", () => RunOutputTask(async () =>
        {
            var initial = new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                InitialInputPreset: new SavedRegion { StartDelay = CaptureStartDelay.ThreeSeconds }) { EnableStartDelay = true };
            var resumed = initial with { Destination = OutputDestination.File };
            List<string> events = [];
            var completion = new CaptureOutcome(null, null);
            using var result = await CaptureSession.SelectWithCountdownAsync(initial, options =>
            {
                events.Add("選択 " + options.Destination);
                Check(options.StartWasDelayed, "待機が撮影開始へ渡らない");
                return Task.FromResult(options.Destination == OutputDestination.Clipboard
                    ? new CaptureOutcome(null, null, delayedRestart: new(CaptureStartDelay.TenSeconds, resumed)) : completion);
            }, (delay, _) =>
            {
                events.Add("待機 " + (int)delay);
                events.Add("表示終了");
                return Task.FromResult(CaptureDelayResult.Ready);
            });
            Check(ReferenceEquals(completion, result), "再開前の中間結果が返った");
            Equal("待機 3,表示終了,選択 Clipboard,待機 10,表示終了,選択 File", string.Join(',', events));

            foreach (var excluded in new[] { initial with { Intent = CaptureIntent.Recording }, initial with { SelectionOnly = true } })
            {
                using var immediate = await CaptureSession.SelectWithCountdownAsync(excluded, options =>
                {
                    Check(!options.StartWasDelayed, "対象外の撮影を遅延した");
                    return Task.FromResult(new CaptureOutcome(null, null));
                }, (_, _) => throw new InvalidOperationException("録画や予約用の選択で待機した"));
            }
        }));

        Run("開始待機の残り秒とバーは同じ経過時間で減り期限付近のEscを優先する", () =>
        {
            foreach (int seconds in new[] { 3, 5, 10 })
            {
                var duration = TimeSpan.FromSeconds(seconds);
                CaptureCountdownFrame At(double elapsed) => CaptureCountdownFrame.FromElapsed(duration, TimeSpan.FromSeconds(elapsed));
                Equal(new CaptureCountdownFrame(seconds, 1), At(-1));
                Equal(new CaptureCountdownFrame(seconds, (seconds - 0.25) / seconds), At(0.25));
                Equal(new CaptureCountdownFrame(seconds - 1, (seconds - 1d) / seconds), At(1));
                Equal(1, At(seconds - 0.001).Seconds);
                Equal<CaptureDelayResult?>(null, At(seconds - 0.001).Result(false));
                Equal(new CaptureCountdownFrame(0, 0), At(seconds));
                Equal<CaptureDelayResult?>(CaptureDelayResult.Ready, At(seconds).Result(false));
                Equal<CaptureDelayResult?>(CaptureDelayResult.Cancelled, At(seconds).Result(true));
                Equal(new CaptureCountdownFrame(0, 0), At(seconds + 1));
            }

            using var countdown = new UI.CaptureCountdownForm(new Rectangle(-1280, 20, 1280, 900), 144);
            Equal(new Rectangle(-162, 839, 144, 63), countdown.Bounds);
            Check(!countdown.ShowInTaskbar, "カウントダウンがタスクバーに出る");
        });

        Run("開始前と再開待機のEscは選択・出力へ進まず次の撮影を妨げない", () => RunOutputTask(async () =>
        {
            var options = new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                InitialInputPreset: new SavedRegion { StartDelay = CaptureStartDelay.ThreeSeconds }) { EnableStartDelay = true };
            int selections = 0;
            Task<CaptureOutcome> Select(CaptureOptions current)
            {
                selections++;
                return Task.FromResult(new CaptureOutcome(null, null, delayedRestart: new(CaptureStartDelay.FiveSeconds, current)));
            }
            foreach (bool restart in new[] { false, true })
            {
                int waits = 0;
                selections = 0;
                using var result = await CaptureSession.SelectWithCountdownAsync(options, Select, (_, _) =>
                    Task.FromResult(++waits == 1 && restart ? CaptureDelayResult.Ready : CaptureDelayResult.Cancelled));
                Equal(restart ? 2 : 1, waits);
                Equal(restart ? 1 : 0, selections);
                Check(result.Selection is null && result.Image is null && result.DelayedRestart is null
                    && !result.EscapedBeforeSelectionInput, "待機の取消が撮影または前回結果の表示へ進んだ");
            }
            using var next = await CaptureSession.SelectWithCountdownAsync(options, current =>
                Task.FromResult(new CaptureOutcome(null, new CaptureSelection(new(2, 3, 4, 5), null, current.Destination))),
                (_, _) => Task.FromResult(CaptureDelayResult.Ready));
            Check(next.Selection is not null, "取消後に選択を開始できない");
        }));

        Run("固定位置の遅延は対象座標を通知し完了後に再解決する。Escは撮影座標を返さない", () => RunOutputTask(async () =>
        {
            var display = new DisplayLayout("fixture", "画面", new(-800, 0, 800, 600), 96, 96);
            var region = RegionCoordinates.FromBounds("登録枠", new(-790, 4, 30, 20), [display]) with
                { StartDelay = CaptureStartDelay.FiveSeconds };
            int reads = 0;
            IReadOnlyList<DisplayLayout> Read() => ++reads == 1 ? [display] : [display with { Bounds = new(800, 0, 800, 600) }];
            foreach (var result in new[] { CaptureDelayResult.Cancelled, CaptureDelayResult.Ready })
            {
                reads = 0;
                var bounds = await CaptureDelay.WaitForRegionAsync(region, Read, (delay, target) =>
                {
                    Equal(region.StartDelay, delay);
                    Equal<Rectangle?>(region.Bounds, target);
                    return Task.FromResult(result);
                });
                Equal(result == CaptureDelayResult.Cancelled ? 1 : 2, reads);
                Equal<Rectangle?>(result == CaptureDelayResult.Cancelled ? null : new Rectangle(810, 4, 30, 20), bounds);
            }
        }));

        Run("右クリックのタイマーは秒数を選ぶと選択を終了して今回の入出力条件を引き継ぐ", () =>
        {
            var frame = MenuFrame(Color.Red);
            var input = SavedRegion.FromSettings("入力", new AppSettings());
            var options = new CaptureOptions(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.File,
                ShowSelectionGuide: false, InitialInputPreset: input) { EnableStartDelay = true };
            var session = new SessionController(options, frame, [], initialPointer: new(80, 80));
            try
            {
                var menu = session.SelectionMenu;
                Equal("待ち時間なし,3秒遅延,5秒遅延,10秒遅延",
                    string.Join(',', menu.Command("CaptureTimerMenu").DropDownItems.Cast<ToolStripItem>().Select(item => item.Text)));
                menu.Command("CaptureDelay0").PerformClick();
                Check(!session.Completion.IsCompleted, "待ち時間なしで選択が終了した");
                session.ChangePadding(20);
                session.ChangeColor(ScreenshotColorDepth.Indexed8);
                session.ChangeAspectRatio(CaptureAspectRatio.Standard);
                menu.Command("CaptureDelay5").PerformClick();
                using var result = session.Completion.GetAwaiter().GetResult();
                Check(result.Selection is null, "タイマー予約で出力を確定した");
                var restart = result.DelayedRestart ?? throw new InvalidOperationException("再開条件を受け取れない");
                Equal(CaptureStartDelay.FiveSeconds, restart.Delay);
                Equal(options.Destination, restart.Options.Destination);
                Equal(input.Id, restart.Options.InitialInputPreset?.Id);
                Equal(20, restart.Options.OutputSettings!.ScreenshotPaddingPixels);
                Equal(ScreenshotColorDepth.Indexed8, restart.Options.OutputSettings.ScreenshotColorDepth);
                Equal(CaptureAspectRatio.Standard, restart.Options.RestartState!.AspectRatio);
                Equal<int?>(20, restart.Options.RestartState.TemporaryPadding);

                var resumed = new SessionController(restart.Options, MenuFrame(Color.Green), [], initialPointer: new(80, 80));
                try
                {
                    Check(resumed.HandleCommandKey(Keys.Escape), "再開後のEscを処理しない");
                    using var cancelled = resumed.Completion.GetAwaiter().GetResult();
                    Check(!cancelled.EscapedBeforeSelectionInput, "タイマー選択後の取消を操作なしとして扱った");
                }
                finally { resumed.Cancel(); }
            }
            finally { session.Cancel(); }
        });

        Run("CLIはタイマー後の画面構成で登録範囲を解決して撮影する", () => WithOutputDirectory(directory => RunOutputTask(async () =>
        {
            var settings = CaptureCommandSettings();
            var display = new DisplayLayout("fixture", "画面", new(0, 0, 800, 600), 96, 96);
            var preset = RegionCoordinates.FromBounds("登録枠", new(3, 4, 30, 20), [display]) with
                { StartDelay = CaptureStartDelay.FiveSeconds };
            settings = settings with { SavedRegions = [.. settings.SavedRegions, preset] };
            var command = CaptureCommandLine.Resolve(new(InputPresetId: preset.Id) { UsePresetMode = true }, settings);
            List<string> events = [];
            using var output = new OutputRouter(directory, _ => { events.Add("copy"); return Task.CompletedTask; }, readDisplays: () => []);
            int exit = await CaptureCommandRunner.ExecuteAsync(command, output, (bounds, _) =>
            {
                events.Add("capture");
                Equal(preset.Bounds, bounds);
                return Task.FromResult<(Bitmap, CaptureMetadata?)>((new Bitmap(bounds.Width, bounds.Height), null));
            }, readPresetDisplays: () => { events.Add("displays"); return [display]; },
                wait: delay => { Equal(TimeSpan.FromSeconds(5), delay); events.Add("wait"); return Task.CompletedTask; });
            Equal(0, exit);
            Equal("wait,displays,capture,copy", string.Join(',', events));
        })));
    }
}
