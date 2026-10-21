using SSCapture.Recording;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void UiTests()
    {
        Run("設定の結果は発生時刻を保持し60秒で消え新しい結果で期限を更新", () =>
        {
            var clock = new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.FromHours(9));
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => [], now: () => clock);
            var result = (TextBox)form.Controls.Find("Status", true).Single();
            var timer = (System.Windows.Forms.Timer)typeof(SettingsForm).GetField("statusTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form)!;
            void Tick() => typeof(System.Windows.Forms.Timer).GetMethod("OnTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(timer, [EventArgs.Empty]);
            form.SetStatus("コピーしました。\n保存しました: C:\\Captures\\結果.png");
            string original = result.Text;
            Check(original.StartsWith(clock.ToLocalTime().ToString("HH:mm:ss"), StringComparison.Ordinal), "発生時刻がない");
            Check(original.Contains("コピーしました。\r\n保存しました:", StringComparison.Ordinal), "改行や本文が失われた");
            clock = clock.AddMilliseconds(59999); Tick(); Equal(original, result.Text);
            clock = clock.AddMilliseconds(1); Tick(); Equal("", result.Text); Check(!timer.Enabled, "期限切れ後もタイマーが有効");
            form.SetStatus("保存できませんでした。", true);
            Check(result.ForeColor.R > result.ForeColor.G, "エラーの色を失った");
            clock = clock.AddSeconds(40);
            form.SetStatus("新しいコピー結果");
            clock = clock.AddSeconds(20); Tick(); Check(result.Text.Contains("新しいコピー結果", StringComparison.Ordinal), "古い期限で新結果を消した");
            clock = clock.AddSeconds(40); Tick(); Equal("", result.Text);
            form.SetStatus("終了時の破棄確認");
            form.Dispose(); Check(!timer.Enabled, "フォーム破棄後もタイマーが有効");
        });
        Run("設定を開き直しても結果の時刻と残り時間を延長しない", () =>
        {
            var occurredAt = new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.FromHours(9));
            var stored = new StatusMessage("保存しました: C:\\Captures\\結果.png", true, occurredAt);
            var clock = occurredAt.AddSeconds(30);
            using var reopened = new SettingsForm(new AppSettings(), _ => null, () => { }, () => [], now: () => clock);
            reopened.SetStatus(stored);
            var result = (TextBox)reopened.Controls.Find("Status", true).Single();
            Equal(stored.DisplayText, result.Text);
            clock = occurredAt.AddMinutes(1);
            reopened.SetStatus(stored); Equal("", result.Text);
            using var later = new SettingsForm(new AppSettings(), _ => null, () => { }, () => [], now: () => clock);
            later.SetStatus(stored); Equal("", later.Controls.Find("Status", true).Single().Text);
        });
        Run("設定の長い複数行結果は省略せずボタンと重ならない", () =>
        {
            using var form = NewSettings();
            string longPath = "C:\\Captures\\" + string.Concat(Enumerable.Repeat("長い保存フォルダ\\", 30)) + "結果.png";
            string text = "コピーしました。\n保存しました: " + longPath + "\n外部アプリへ送りました。";
            form.SetStatus(text);
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var control in Descendants(form)) _ = control.Handle; LayoutTree(form);
            var result = (TextBox)form.Controls.Find("Status", true).Single();
            Check(result.Multiline && result.ReadOnly && result.WordWrap && result.ScrollBars == ScrollBars.Vertical, "長文を読める設定でない");
            Check(result.Text.EndsWith(text.ReplaceLineEndings("\r\n"), StringComparison.Ordinal), "本文が省略された");
            Check(result.Width >= 1000 && result.Height >= result.Font.Height * 3, "結果表示の領域不足");
            Check(result.Parent!.ClientRectangle.Contains(result.Bounds), "結果が親領域外");
            var saveButton = form.Controls.Find("SaveSettings", true).Single();
            Check(result.Bottom <= saveButton.Parent!.Top, "結果とボタンが重なる");
            Check(result.SelectionStart == 0, "新しい結果の先頭が見えない");
            var originalSize = result.Size;
            result.Dock = DockStyle.None; result.Size = new Size(240, 20); LayoutTree(form);
            Check(result.Width < 1000 && result.Height < result.Font.Height * 3, "表示領域の断線を検出できない");
            result.Dock = DockStyle.Fill; LayoutTree(form); Equal(originalSize, result.Size);
        });
        Run("通知の画面と位置・整数秒数・効果音を独立して保存できる", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings { NotificationDisplay = NotificationDisplay.Display10,
                NotificationDurationSeconds = 12, PlayCaptureSound = false }, value => { saved = value; return null; }, () => { }, () => []);
            var display = (ComboBox)form.Controls.Find("NotificationDisplay", true).Single();
            var corner = (ComboBox)form.Controls.Find("NotificationCorner", true).Single();
            var duration = (NumericUpDown)form.Controls.Find("NotificationDurationSeconds", true).Single();
            Equal((int)NotificationDisplay.Display10, display.SelectedIndex);
            Equal(12m, duration.Value);
            Equal(0m, duration.Minimum); Equal(3600m, duration.Maximum); Equal(0, duration.DecimalPlaces); Equal(1m, duration.Increment);
            Equal(12, display.Items.Count);
            Equal("メイン画面", display.Items[0]!.ToString()); Equal("撮影した画面", display.Items[1]!.ToString());
            Equal("DISPLAY1", display.Items[2]!.ToString()); Equal("DISPLAY10", display.Items[11]!.ToString());
            Equal(0, form.Controls.Find("ShowCaptureNotification", true).Length);
            duration.Value = 0;
            display.SelectedIndex = (int)NotificationDisplay.Main;
            ((CheckBox)form.Controls.Find("PlayCaptureSound", true).Single()).Checked = true;
            corner.SelectedIndex = (int)NotificationCorner.BottomLeft;
            typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
            Check(saved is not null && !saved.ShowCaptureNotification && saved.PlayCaptureSound, "表示と音が独立して保存されない");
            Equal(NotificationCorner.BottomLeft, saved!.NotificationCorner);
            Equal(NotificationDisplay.Main, saved.NotificationDisplay); Equal(0, saved.NotificationDurationSeconds);
            duration.Value = 3600;
            ((CheckBox)form.Controls.Find("PlayCaptureSound", true).Single()).Checked = false;
            foreach (var target in Enum.GetValues<NotificationDisplay>())
            {
                display.SelectedIndex = (int)target;
                typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
                Equal(target, saved.NotificationDisplay); Equal(3600, saved.NotificationDurationSeconds);
                Check(saved.ShowCaptureNotification && !saved.PlayCaptureSound, "表示ON・音OFFを保持できない");
            }
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "通知");
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var child in Descendants(form)) _ = child.Handle; LayoutTree(form);
            var notificationLayout = (TableLayoutPanel)display.Parent!;
            Equal(0, notificationLayout.GetRow(display)); Equal(1, notificationLayout.GetRow(corner));
            Equal(2, notificationLayout.GetRow(duration.Parent!));
            foreach (string name in new[] { "NotificationDisplay", "NotificationCorner", "NotificationDurationSeconds", "NotificationDurationUnit", "PlayCaptureSound" })
            {
                var control = form.Controls.Find(name, true).Single();
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), name + " が親領域外");
                Check(control.Height >= 20, name + " の領域不足");
            }
            var info = form.Controls.Find("NotificationInfo", true).Single();
            Check(info.Parent!.ClientRectangle.Contains(info.Bounds), "通知の制限表示が領域外");
            var measured = TextRenderer.MeasureText(info.Text, info.Font, new Size(info.Width, int.MaxValue), TextFormatFlags.WordBreak);
            Check(measured.Height <= info.Height, "通知の制限表示が切れる");
        });
        Run("固定ピクセル・基点・カーソル・履歴・前回キーの設定を保存", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings { FixedCaptureAnchor = CaptureAnchor.TopRight }, value => { saved = value; return null; }, () => { }, () => []);
            ((NumericUpDown)form.Controls.Find("FixedWidth", true).Single()).Value = 321;
            ((NumericUpDown)form.Controls.Find("FixedHeight", true).Single()).Value = 123;
            var anchor = (ComboBox)form.Controls.Find("FixedCaptureAnchor", true).Single();
            Equal((int)CaptureAnchor.TopRight, anchor.SelectedIndex);
            Check(anchor.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(new[] { "左上", "右上", "右下", "左下" }), "基点4隅の選択肢");
            anchor.SelectedIndex = (int)CaptureAnchor.BottomRight;
            ((CheckBox)form.Controls.Find("IncludeCursor", true).Single()).Checked = true;
            ((CheckBox)form.Controls.Find("KeepHistory", true).Single()).Checked = false;
            typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
            Check(saved is not null, "設定未保存"); Equal(321, saved!.FixedWidth); Equal(123, saved.FixedHeight);
            Check(saved.IncludeCursor && !saved.KeepHistory, "チェック項目が反映されない");
            Equal(CaptureAnchor.BottomRight, saved.FixedCaptureAnchor);
            Equal("Win+Ctrl+Shift+S", saved.LastRegionShortcut);
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "入力");
            form.Size = form.MinimumSize;
            _ = form.Handle; foreach (var child in Descendants(form)) _ = child.Handle; LayoutTree(form);
            foreach (string name in new[] { "FixedWidth", "FixedHeight", "FixedCaptureAnchor", "IncludeCursor", "LimitToStartingMonitor", "DimCaptureBackground" })
            {
                var control = form.Controls.Find(name, true).Single();
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), name + " が親領域外");
            }
            Equal(8, ((TableLayoutPanel)anchor.Parent!).RowCount);
            tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "履歴"); LayoutTree(form);
            foreach (string name in new[] { "KeepHistory", "OpenHistory" })
            { var field = form.Controls.Find(name, true).Single(); Check(field.Parent!.ClientRectangle.Contains(field.Bounds), name + "が履歴区分に収まらない"); }
        });
        Run("設定・OCRは閉じるだけで未完了ウィンドウを再利用", () =>
        {
            using var settings = NewSettings();
            using var bitmap = new Bitmap(4, 4);
            using var ocr = new OcrNameForm(bitmap, new AppSettings(), CaptureIntent.Screenshot, DateTimeOffset.Now);
            foreach (Form window in new Form[] { settings, ocr })
                Check(window.ControlBox && !window.MinimizeBox && !window.MaximizeBox, "タイトルバーのボタン不正");
            int shown = 0;
            Check(WindowVisibility.RestorePending(ocr, window => { Check(ReferenceEquals(window, ocr), "既存窓でない"); shown++; }), "新規処理を拒否しない");
            Check(WindowVisibility.RestorePending(ocr, _ => shown++), "再押下でも拒否");
            Equal(2, shown);
            ocr.Dispose();
            Check(!WindowVisibility.RestorePending(ocr, _ => shown++), "終了済み窓を再表示");
            Check(!WindowVisibility.RestorePending(null), "未処理なしで拒否");
        });
        Run("設定10タブの入力欄とボタンが表示領域に収まる", () =>
        {
            using var form = NewSettings();
            Equal("設定 - SSCapture - ver 0.1.4", form.Text);
            form.Size = form.MinimumSize;
            _ = form.Handle;
            foreach (Control child in Descendants(form)) _ = child.Handle;
            LayoutTree(form);
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            var categories = (ListBox)form.Controls.Find("SettingsCategories", true).Single();
            Equal(tabs.TabCount, categories.Items.Count);
            Check(categories.Parent!.ClientRectangle.Contains(categories.Bounds), "分類一覧が領域外");
            Check(!Descendants(form).OfType<Label>().Any(label => label.Text.Contains("SSCapture", StringComparison.Ordinal)), "アプリ名見出しが残っている");
            Equal(0, ((TableLayoutPanel)tabs.Parent!).GetRow(tabs));
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                LayoutTree(form);
                foreach (var input in Descendants(page).Where(c => c is TextBox or ComboBox && c.Parent is not UpDownBase))
                {
                    Check(input.Width >= (input.Name.EndsWith("EditorPreset", StringComparison.Ordinal) ? 160 : input.Name.EndsWith("Key", StringComparison.Ordinal) ? 200 : page.Text == "入力プリセット" ? 300 : 420), $"{input.Name} 幅 {input.Width}");
                    Check(input.Height >= 24, $"{input.Name} 高さ {input.Height}");
                    Check(input.Parent!.ClientRectangle.Contains(input.Bounds), $"{input.Name} が親領域外: {input.Bounds} parent={input.Parent.ClientRectangle}");
                }
                foreach (var label in Descendants(page).OfType<Label>().Where(label =>
                    label.Parent is TableLayoutPanel table && table.GetColumn(label) == 0 && table.GetColumnSpan(label) == 1))
                {
                    Size preferred = label.GetPreferredSize(Size.Empty);
                    Check(label.Width >= preferred.Width && label.Height >= preferred.Height, $"項目名 {label.Text} が切れる: {label.Size} preferred={preferred}");
                }
            }
            foreach (string name in new[] { "SaveSettings", "CloseSettings", "OpenFolder" })
            {
                var button = form.Controls.Find(name, true).Single();
                Check(button.Width >= 90 && button.Height >= 32, $"{name} サイズ不正");
                Check(button.Parent!.ClientRectangle.Contains(button.Bounds), $"{name} が親領域外");
            }
            tabs.SelectedIndex = 0;
            var folderToggle = form.Controls.Find("OpenFolderAfterSave", true).Single();
            Check(folderToggle.Parent!.ClientRectangle.Contains(folderToggle.Bounds), "保存後のチェック項目が領域外");
            Check(folderToggle.Height >= 24 && folderToggle.Width >= 300, "保存後のチェック項目が小さい");
            var layout = form.Controls.Find("SettingsLayout", true).Single();
            layout.Dock = DockStyle.None;
            layout.Size = new Size(240, 180);
            LayoutTree(form);
            Check(form.Controls.Find("ScreenshotPaddingPixels", true).Single().Width < 420, "Dock断線を寸法検査で検出できない");
        });
        Run("撮影モード5種と未接続マイクIDが設定に保持される", () =>
        {
            foreach (var captureMode in Enum.GetValues<CaptureMode>())
            {
                using var form = NewSettings(new AppSettings { DefaultMode = captureMode, MicrophoneDeviceId = "missing" });
                var combo = (ComboBox)form.Controls.Find("CaptureMode", true).Single();
                Equal(5, combo.Items.Count);
                Equal((int)captureMode, combo.SelectedIndex);
                var microphones = (ComboBox)form.Controls.Find("MicrophoneDevice", true).Single();
                Equal("missing", ((MicrophoneInfo)microphones.SelectedItem!).Id);
            }
        });
        Run("録画操作窓の状態と停止ボタンに十分な領域がある", () =>
        {
            using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings());
            form.CreateControl(); LayoutTree(form);
            var state = form.Controls.Find("RecordingState", true).Single();
            Check(state.Width >= 400 && state.Height >= 28, "録画状態表示の領域不足");
            form.SetRecording(); LayoutTree(form);
            var stop = form.Controls.Find("StopRecording", true).Single();
            Check(stop.Width >= 90 && stop.Height >= 32, "停止ボタンの領域不足");
            Check(stop.Parent!.ClientRectangle.Contains(stop.Bounds), "停止ボタンが親領域外");
            form.SetProcessing("処理中");
            Check(!stop.Enabled, "確定中に再停止可能");
        });
        Run("録画の開始・停止・一時停止・再開を状態に応じて要求する", () =>
        {
            using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings(), false);
            int starts = 0, stops = 0, cancels = 0, pauses = 0, resumes = 0;
            form.StartRequested += () => starts++;
            form.StopRequested += () => stops++;
            form.CancelRequested += () => cancels++;
            form.PauseRequested += () => pauses++;
            form.ResumeRequested += () => resumes++;
            var start = (Button)form.Controls.Find("StartRecording", true).Single();
            var stop = (Button)form.Controls.Find("StopRecording", true).Single();
            var pause = (Button)form.Controls.Find("PauseRecording", true).Single();
            var cancel = (Button)form.Controls.Find("CancelRecording", true).Single();
            Check(start.Enabled && cancel.Enabled && !stop.Enabled && !pause.Enabled, "準備中の操作が不正");
            Check(ReferenceEquals(start, form.AcceptButton) && ReferenceEquals(cancel, form.CancelButton), "準備時のEnter/Escapeが不正");
            RecordingClick(start); RecordingClick(cancel); RecordingClick(stop); RecordingClick(pause);
            Equal(1, starts); Equal(1, cancels); Equal(0, stops); Equal(0, pauses); Equal(0, resumes);
            form.SetRecording();
            Check(stop.Enabled && pause.Enabled && !start.Enabled && !cancel.Enabled, "録画中の操作が不正");
            Check(form.AcceptButton is null && form.CancelButton is null, "録画中に開始/キャンセルが既定操作として残った");
            Equal("一時停止", pause.Text);
            RecordingClick(start); RecordingClick(cancel); RecordingClick(pause); RecordingClick(stop);
            Equal(1, starts); Equal(1, cancels); Equal(1, pauses); Equal(1, stops); Equal(0, resumes);
            form.SetPaused();
            Equal("再開", pause.Text);
            Check(form.Controls.Find("RecordingState", true).Single().Text.Contains("一時停止", StringComparison.Ordinal), "一時停止の状態表示がない");
            RecordingClick(pause); RecordingClick(stop);
            Equal(1, resumes); Equal(2, stops);
            form.SetRecording();
            Equal("一時停止", pause.Text);
            Check(form.Controls.Find("RecordingState", true).Single().Text.Contains("録画中", StringComparison.Ordinal), "再開後の状態表示がない");
        });
        Run("PC音とマイクは全待機状態で個別に切替でき反映時はイベントを再発火しない", () =>
        {
            using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings(), false);
            var system = (CheckBox)form.Controls.Find("SystemAudioToggle", true).Single();
            var mic = (CheckBox)form.Controls.Find("MicrophoneToggle", true).Single();
            var systemChanges = new List<bool>(); var micChanges = new List<bool>();
            form.SystemAudioChangeRequested += systemChanges.Add;
            form.MicrophoneChangeRequested += micChanges.Add;
            Equal(Appearance.Button, system.Appearance); Equal(Appearance.Button, mic.Appearance);
            for (int mode = 0; mode < 3; mode++)
            {
                if (mode == 1) form.SetRecording(); else if (mode == 2) form.SetPaused();
                form.SetAudioState(false, true);
                Equal(mode, systemChanges.Count); Equal(mode, micChanges.Count);
                Equal("PC音: OFF", system.Text); Equal("マイク: ON", mic.Text);
                Check(system.Enabled && mic.Enabled, "音声操作が無効");
                RecordingClick(system); Equal(mode + 1, systemChanges.Count); Equal(mode, micChanges.Count);
                Check(systemChanges[^1] && system.Checked, "PC音のON要求が不正");
                RecordingClick(mic); Equal(mode + 1, micChanges.Count);
                Check(!micChanges[^1] && !mic.Checked, "マイクのOFF要求が不正");
                Equal("PC音: ON", system.Text); Equal("マイク: OFF", mic.Text);
                form.SetAudioState(false, true);
                Equal(mode + 1, systemChanges.Count); Equal(mode + 1, micChanges.Count);
            }
        });
        Run("録画処理中は全操作を拒否し準備・録画・一時停止へ戻せる", () =>
        {
            using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings(), false);
            int requests = 0;
            form.StartRequested += () => requests++; form.StopRequested += () => requests++;
            form.CancelRequested += () => requests++; form.PauseRequested += () => requests++;
            form.ResumeRequested += () => requests++; form.SystemAudioChangeRequested += _ => requests++;
            form.MicrophoneChangeRequested += _ => requests++;
            string[] names = ["StartRecording", "StopRecording", "CancelRecording", "PauseRecording", "SystemAudioToggle", "MicrophoneToggle"];
            for (int mode = 0; mode < 3; mode++)
            {
                if (mode == 1) form.SetRecording(); else if (mode == 2) form.SetPaused();
                form.SetProcessing("音声設定を反映中", false);
                Equal("音声設定を反映中", form.Controls.Find("RecordingState", true).Single().Text);
                Check(form.AcceptButton is null && form.CancelButton is null, "処理中にEnter/Escapeの既定操作が残った");
                foreach (string name in names)
                {
                    Control operation = form.Controls.Find(name, true).Single();
                    Check(!operation.Enabled, name + " が処理中も有効");
                    RecordingClick(operation);
                }
                form.SetAudioState(true, false);
                Equal(0, requests);
                form.SetControlsReady();
                Check(form.Controls.Find("SystemAudioToggle", true).Single().Enabled && form.Controls.Find("MicrophoneToggle", true).Single().Enabled, "音声操作が復帰しない");
                Equal(mode == 0, form.Controls.Find("StartRecording", true).Single().Enabled);
                Equal(mode != 0, form.Controls.Find("StopRecording", true).Single().Enabled);
                Equal(mode == 2 ? "再開" : "一時停止", form.Controls.Find("PauseRecording", true).Single().Text);
            }
        });
        Run("一時停止の時計は止まり再開・音声切替で累積時間を失わない", () =>
        {
            using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings(), false);
            var elapsed = RecordingField<System.Diagnostics.Stopwatch>(form, "elapsed");
            var timer = RecordingField<System.Windows.Forms.Timer>(form, "timer");
            Check(!elapsed.IsRunning && !timer.Enabled, "準備中に時計が進む");
            form.SetRecording();
            Check(elapsed.IsRunning && timer.Enabled, "開始後の時計が動かない");
            Check(SpinWait.SpinUntil(() => elapsed.ElapsedMilliseconds >= 20, 1000), "テスト時計が進まない");
            form.SetPaused();
            long accumulated = elapsed.ElapsedTicks;
            Check(accumulated > 0 && !elapsed.IsRunning && !timer.Enabled, "一時停止中に時計が動く");
            form.SetProcessing("マイク変更中", false); form.SetControlsReady();
            Equal(accumulated, elapsed.ElapsedTicks);
            Check(!elapsed.IsRunning && !timer.Enabled, "音声変更後に一時停止が解除された");
            form.SetRecording();
            Check(elapsed.ElapsedTicks >= accumulated && elapsed.IsRunning && timer.Enabled, "再開で累積時間をリセットした");
            form.SetProcessing("PC音変更中", false);
            Check(elapsed.IsRunning && !timer.Enabled, "映像継続中の音声変更で時計を停止した");
            long duringChange = elapsed.ElapsedTicks;
            form.SetControlsReady();
            Check(elapsed.ElapsedTicks >= duringChange && elapsed.IsRunning && timer.Enabled, "音声変更の復帰で累積時間を失った");
            form.SetProcessing("録画を確定中");
            Check(!elapsed.IsRunning && !timer.Enabled, "確定処理時間を録画時間に含める");
            accumulated = elapsed.ElapsedTicks;
            form.SetControlsReady();
            Check(elapsed.ElapsedTicks >= accumulated && elapsed.IsRunning && timer.Enabled, "処理後の時計が復帰しない");
            form.Dispose();
            Check(!elapsed.IsRunning && !timer.Enabled, "破棄後も時計が動く");
        });
        Run("録画操作窓の閉じるは準備でキャンセル・録画と一時停止で停止・処理中は拒否", () =>
        {
            using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings(), false);
            int stopped = 0, canceled = 0;
            form.StopRequested += () => stopped++; form.CancelRequested += () => canceled++;
            Check(RecordingClose(form).Cancel, "準備中に確定せず閉じた"); Equal(1, canceled); Equal(0, stopped);
            form.SetRecording();
            Check(RecordingClose(form).Cancel, "録画中に確定せず閉じた"); Equal(1, stopped);
            form.SetPaused();
            Check(RecordingClose(form).Cancel, "一時停止中に確定せず閉じた"); Equal(2, stopped);
            form.SetProcessing("確定中");
            Check(RecordingClose(form).Cancel, "処理中に閉じた"); Equal(2, stopped); Equal(1, canceled);
        });
        Run("録画操作窓の音声トグルと一時停止は12pt・16ptでも文字と領域が収まる", () =>
        {
            foreach (float pointSize in new[] { 12F, 16F })
            {
                using var font = new Font("Yu Gothic UI", pointSize);
                using var form = new RecordingControlForm(new Rectangle(0, 0, 640, 480), new AppSettings(), false) { Font = font };
                _ = form.Handle; foreach (var child in Descendants(form)) _ = child.Handle;
                for (int mode = 0; mode < 3; mode++)
                {
                    if (mode == 1) form.SetRecording(); else if (mode == 2) form.SetPaused();
                    LayoutTree(form);
                    string[] names = mode == 0
                        ? ["RecordingState", "SystemAudioToggle", "MicrophoneToggle", "StartRecording", "CancelRecording"]
                        : ["RecordingState", "SystemAudioToggle", "MicrophoneToggle", "StopRecording", "PauseRecording"];
                    foreach (string name in names)
                    {
                        var control = form.Controls.Find(name, true).Single();
                        Check(control.Parent!.ClientRectangle.Contains(control.Bounds), $"{pointSize}pt {name} が領域外: {control.Bounds} parent={control.Parent.ClientRectangle}");
                        Size measured = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.SingleLine);
                        Check(control.Width >= measured.Width + control.Padding.Horizontal && control.Height >= measured.Height + control.Padding.Vertical,
                            $"{pointSize}pt {name} の文字が切れる: {control.Size} text={measured} padding={control.Padding}");
                        if (control is Button)
                            Check(control.Bottom + control.Margin.Bottom <= control.Parent.ClientSize.Height,
                                $"{pointSize}pt {name} の下余白が収まらない: {control.Bounds} margin={control.Margin} parent={control.Parent.ClientRectangle}");
                    }
                    var system = form.Controls.Find("SystemAudioToggle", true).Single();
                    var mic = form.Controls.Find("MicrophoneToggle", true).Single();
                    Check(system.Width >= 200 && mic.Width >= 200 && system.Right <= mic.Left, "音声操作の幅不足または重なり");
                    Equal(system.Top, mic.Top); Equal(system.Height, mic.Height);
                    if (mode != 0)
                    {
                        var pause = form.Controls.Find("PauseRecording", true).Single();
                        var stop = form.Controls.Find("StopRecording", true).Single();
                        Check(!pause.Bounds.IntersectsWith(stop.Bounds), "停止と一時停止が重なる");
                        Equal(stop.Top, pause.Top);
                    }
                }
                var layout = form.Controls.Find("RecordingLayout", true).Single();
                Size goodSize = layout.Size;
                layout.Dock = DockStyle.None; layout.Size = new Size(240, 100); LayoutTree(form);
                Check(form.Controls.Find("SystemAudioToggle", true).Single().Width < 200, "録画UIのDock断線を検出できない");
                layout.Dock = DockStyle.Fill; LayoutTree(form); Equal(goodSize, layout.Size);
                using var normalFont = new Font("Yu Gothic UI", 12F);
                form.Font = normalFont; LayoutTree(form);
                Equal(178, form.ClientSize.Height);
            }
        });
    }
    private static void RecordingClick(Control control) => control.GetType().GetMethod("OnClick",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(control, [EventArgs.Empty]);
    private static T RecordingField<T>(RecordingControlForm form, string name) => (T)typeof(RecordingControlForm)
        .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(form)!;
    private static FormClosingEventArgs RecordingClose(RecordingControlForm form)
    {
        var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
        typeof(Form).GetMethod("OnFormClosing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, [closing]);
        return closing;
    }
    private static SettingsForm NewSettings(AppSettings? settings = null) => new(settings ?? new AppSettings(), _ => null, () => { }, () => [new MicrophoneInfo("test-mic", "テストマイク")]);
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void LayoutTree(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls) LayoutTree(child);
    }
}
