using System.Reflection;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings initial;
    private readonly TextBox directory, imageEditor, videoEditor, imageArguments, videoArguments, fileNamePattern, ocrSeparator, oneOcrDirectory;
    private readonly HotkeyEditor screenshot1, screenshot2, recording1, recording2, lastRegionKey;
    private readonly NumericUpDown fixedWidth, fixedHeight, notificationDuration, screenshotPadding;
    private readonly ComboBox screenshotColorDepth;
    private readonly CheckBox dimCaptureBackground;
    private readonly CheckBox includeCursor, keepHistory, playCaptureSound, limitToStartingMonitor;
    private readonly ComboBox notificationCorner, notificationDisplay, fixedCaptureAnchor;
    private readonly HotkeyEditor[] instantKeys = new HotkeyEditor[10];
    private readonly ComboBox mode, microphone;
    private readonly CheckBox clipboardOutput, fileOutput, externalOutput, freeze, systemAudio, micAudio, openFolderAfterSave;
    private readonly TextBox status;
    private readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 1000 };
    private readonly ToolTip settingTips = new() { AutoPopDelay = 25000, InitialDelay = 500, ReshowDelay = 100, ShowAlways = true };
    private readonly Func<DateTimeOffset> now;
    private StatusMessage? statusMessage;
    private readonly Func<AppSettings, string?> save;
    private readonly CapturePresetEditor presetEditor;

    public SettingsForm(AppSettings settings, Func<AppSettings, string?> onSave, Action openFolder,
        Func<IReadOnlyList<MicrophoneInfo>>? listMicrophones = null, Action? openRegions = null, Action? openHistory = null,
        Func<DateTimeOffset>? now = null, Func<IReadOnlyList<DisplayTarget>>? listDisplays = null)
    {
        initial = settings;
        save = onSave;
        this.now = now ?? (() => DateTimeOffset.Now);
        Text = WindowTitle.Create("設定");
        Name = "SettingsForm";
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 12F);
        BackColor = Color.FromArgb(247, 249, 251);
        ForeColor = Color.FromArgb(28, 37, 47);
        ClientSize = new Size(1080, 582);
        MinimumSize = SizeFromClientSize(ClientSize);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();
        var layout = new TableLayoutPanel { Name = "SettingsLayout", Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 0f, 96f, 38f }) layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        Controls.Add(layout);
        var tabs = new TabControl { Name = "SettingsTabs", Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout.Controls.Add(tabs, 0, 0);
        var basic = Page(tabs, "基本");
        directory = Field(basic, "保存先", "SaveDirectory", settings.SaveDirectory, 0);
        mode = Combo(basic, "撮影モード", "CaptureMode", 1);
        foreach (var value in Enum.GetValues<CaptureMode>()) mode.Items.Add(new ModeItem(value));
        mode.SelectedIndex = (int)settings.DefaultMode;
        var destinations = new FlowLayoutPanel { WrapContents = false, Margin = Padding.Empty };
        clipboardOutput = OutputToggle("クリップボード", "ClipboardOutput", OutputDestination.Clipboard);
        fileOutput = OutputToggle("ファイル保存", "FileOutput", OutputDestination.File);
        externalOutput = OutputToggle("外部アプリ", "ExternalOutput", OutputDestination.ExternalApp);
        destinations.Controls.AddRange([clipboardOutput, fileOutput, externalOutput]);
        Add(basic, "出力先", destinations, 2);
        CheckBox OutputToggle(string text, string name, OutputDestination value) => new()
        { Text = text, Name = name, AutoSize = true, Checked = settings.DefaultDestination.HasFlag(value), Margin = new Padding(0, 3, 12, 0) };
        freeze = Toggle(basic, "撮影タイミング", "FreezeFrame", "撮影開始時の画面を固定", settings.FreezeFrame, 3);
        openFolderAfterSave = Toggle(basic, "保存後", "OpenFolderAfterSave", "保存フォルダを開く", settings.OpenFolderAfterSave, 4);
        limitToStartingMonitor = Toggle(basic, "範囲選択", "LimitToStartingMonitor", "開始画面内だけで選択", settings.LimitToStartingMonitor, 5);
        basic.RowCount = 8;
        basic.RowStyles.Insert(5, new RowStyle(SizeType.Absolute, 40));
        basic.RowStyles.Insert(6, new RowStyle(SizeType.Absolute, 40));
        screenshotPadding = new NumericUpDown { Name = "ScreenshotPaddingPixels", Minimum = 0,
            Maximum = ScreenshotPadding.MaximumPixels, DecimalPlaces = 0, Increment = 1, Value = settings.ScreenshotPaddingPixels };
        Add(basic, "画像の余白 (px)", screenshotPadding, 6);
        basic.RowCount = 9;
        basic.RowStyles.Insert(7, new RowStyle(SizeType.Absolute, 40));
        screenshotColorDepth = Combo(basic, "静止画の減色", "ScreenshotColorDepth", 7);
        screenshotColorDepth.Items.AddRange(["減色なし", "256色（8ビット）", "16色（4ビット）"]);
        screenshotColorDepth.SelectedIndex = (int)settings.ScreenshotColorDepth;
        basic.RowCount = 10;
        basic.RowStyles.Insert(8, new RowStyle(SizeType.Absolute, 40));
        dimCaptureBackground = Toggle(basic, "背景表示", "DimCaptureBackground", "撮影中に背景を暗くする", settings.DimCaptureBackground, 8);
        var naming = Page(tabs, "ファイル名");
        fileNamePattern = Field(naming, "パターン", "FileNamePattern", settings.FileNamePattern, 0);
        ocrSeparator = Field(naming, "OCR区切り", "OcrSeparator", settings.OcrSeparator, 1);
        oneOcrDirectory = Field(naming, "OneOCRフォルダ", "OneOcrDirectory", settings.OneOcrDirectory, 2);
        oneOcrDirectory.PlaceholderText = "空欄でSnipping Toolから自動検出";
        var namingHelp = new Label { Dock = DockStyle.Fill, Text = "{yyyymmdd} 年月日　{hhmmss} 時分秒　{ocr} 文字列\n{region} 範囲名　{#} 1,2…　{##} 01,02…　動画は.mp4" };
        naming.Controls.Add(namingHelp, 0, 3); naming.SetColumnSpan(namingHelp, 2); naming.SetRowSpan(namingHelp, 2);
        var keys = Page(tabs, "ショートカット");
        screenshot1 = KeyField(keys, "撮影 1", "ScreenshotShortcut1", settings.ScreenshotShortcut1, 0);
        screenshot2 = KeyField(keys, "撮影 2", "ScreenshotShortcut2", settings.ScreenshotShortcut2, 1);
        recording1 = KeyField(keys, "録画 1", "RecordingShortcut1", settings.RecordingShortcut1, 2);
        recording2 = KeyField(keys, "録画 2", "RecordingShortcut2", settings.RecordingShortcut2, 3);
        lastRegionKey = KeyField(keys, "前回の範囲", "LastRegionShortcut", settings.LastRegionShortcut, 4);
        var regions = Page(tabs, "範囲・履歴");
        regions.RowCount = 7;
        regions.RowStyles.Insert(5, new RowStyle(SizeType.Absolute, 40));
        fixedWidth = PixelField(regions, "固定幅 (px)", "FixedWidth", settings.FixedWidth, 0);
        fixedHeight = PixelField(regions, "固定高さ (px)", "FixedHeight", settings.FixedHeight, 1);
        fixedCaptureAnchor = Combo(regions, "固定サイズの基点", "FixedCaptureAnchor", 2);
        foreach (var anchor in Enum.GetValues<CaptureAnchor>()) fixedCaptureAnchor.Items.Add(new CaptureAnchorItem(anchor));
        fixedCaptureAnchor.SelectedIndex = (int)settings.FixedCaptureAnchor;
        includeCursor = Toggle(regions, "カーソル", "IncludeCursor", "画像・動画に含める", settings.IncludeCursor, 3);
        keepHistory = Toggle(regions, "撮影履歴", "KeepHistory", "メモリーに保持する", settings.KeepHistory, 4);
        var regionButtons = new FlowLayoutPanel { WrapContents = false, Margin = Padding.Empty };
        var manageRegions = MakeButton("登録範囲", "OpenRegions", ButtonRole.Secondary);
        manageRegions.Width = 144; manageRegions.Enabled = openRegions is not null;
        manageRegions.Click += (_, _) => openRegions?.Invoke();
        var historyButton = MakeButton("撮影履歴", "OpenHistory", ButtonRole.Secondary);
        historyButton.Width = 144; historyButton.Enabled = openHistory is not null;
        historyButton.Click += (_, _) => openHistory?.Invoke();
        regionButtons.Controls.AddRange([manageRegions, historyButton]);
        Add(regions, "管理", regionButtons, 5);
        regionButtons.Margin = Padding.Empty;
        var instant = Page(tabs, "即時撮影");
        ((TabPage)instant.Parent!).AutoScroll = true;
        instant.Dock = DockStyle.Top;
        instant.Height = 440;
        instant.RowCount = 11;
        instant.RowStyles.Clear();
        for (int row = 0; row < 11; row++) instant.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var displaySummary = new Label { Name = "InstantDisplays", Dock = DockStyle.Fill, AutoEllipsis = true };
        instant.Controls.Add(displaySummary, 0, 0); instant.SetColumnSpan(displaySummary, 2);
        var displayLabels = new Label[10];
        for (int index = 0; index < 10; index++)
        {
            instantKeys[index] = KeyField(instant, $"画面{index + 1}", $"InstantShortcut{index + 1}", settings.InstantShortcuts[index], index + 1);
            displayLabels[index] = (Label)instant.GetControlFromPosition(0, index + 1)!;
            displayLabels[index].Name = $"InstantDisplayLabel{index + 1}";
        }
        void RefreshInstantDisplays()
        {
            var displays = InstantCapture.OrderDisplays((listDisplays ?? InstantCapture.ReadDisplays)());
            displaySummary.Text = displays.Count == 0 ? "接続中の画面はありません。" : "接続中: " + string.Join(" / ", displays.Take(10).Select((display, index) =>
                $"画面{index + 1}{(display.IsPrimary ? "（主）" : "")}: {display.DeviceName.Replace(@"\\.\", "")} ({display.Bounds.Width}×{display.Bounds.Height})"));
            for (int index = 0; index < displayLabels.Length; index++)
                displayLabels[index].Text = $"画面{index + 1}" + (index >= displays.Count ? "（未接続）" : displays[index].IsPrimary ? "（主）" : "");
        }
        RefreshInstantDisplays();
        Activated += (_, _) => RefreshInstantDisplays();
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == instant.Parent) RefreshInstantDisplays(); };
        var audio = Page(tabs, "録画・音声");
        systemAudio = Toggle(audio, "PCの再生音", "SystemAudio", "録音する", settings.RecordSystemAudio, 0);
        micAudio = Toggle(audio, "マイク", "MicrophoneAudio", "録音する", settings.RecordMicrophone, 1);
        microphone = Combo(audio, "使用するマイク", "MicrophoneDevice", 2);
        microphone.DisplayMember = nameof(MicrophoneInfo.Name);
        microphone.Items.Add(new MicrophoneInfo("", "既定のマイク"));
        string? deviceError = null;
        try { foreach (var item in (listMicrophones ?? RecordingService.GetMicrophones)()) microphone.Items.Add(item); }
        catch (Exception ex) { deviceError = "マイク一覧を取得できません: " + ex.Message; }
        int selectedMic = Enumerable.Range(0, microphone.Items.Count).FirstOrDefault(i => ((MicrophoneInfo)microphone.Items[i]!).Id == settings.MicrophoneDeviceId, -1);
        if (selectedMic < 0)
        {
            microphone.Items.Add(new MicrophoneInfo(settings.MicrophoneDeviceId, "未接続のマイク"));
            selectedMic = microphone.Items.Count - 1;
        }
        microphone.SelectedIndex = selectedMic;
        microphone.Enabled = micAudio.Checked;
        micAudio.CheckedChanged += (_, _) => microphone.Enabled = micAudio.Checked;
        (imageEditor, imageArguments) = EditorPage(tabs, "画像編集アプリ", "Image", settings.ImageEditorPath, settings.ImageEditorArguments, EditorPresets.Images);
        (videoEditor, videoArguments) = EditorPage(tabs, "動画編集アプリ", "Video", settings.VideoEditorPath, settings.VideoEditorArguments, EditorPresets.Videos);
        var notifications = Page(tabs, "通知");
        notifications.RowCount = 5;
        notifications.RowStyles.Clear();
        for (int row = 0; row < 4; row++) notifications.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        notifications.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        notificationDisplay = Combo(notifications, "表示先", "NotificationDisplay", 0);
        foreach (var display in Enum.GetValues<NotificationDisplay>()) notificationDisplay.Items.Add(new NotificationDisplayItem(display));
        notificationDisplay.SelectedIndex = (int)settings.NotificationDisplay;
        notificationCorner = Combo(notifications, "表示位置", "NotificationCorner", 1);
        foreach (var corner in Enum.GetValues<NotificationCorner>()) notificationCorner.Items.Add(new CornerItem(corner));
        notificationCorner.SelectedIndex = (int)settings.NotificationCorner;
        var durationRow = new FlowLayoutPanel { WrapContents = false, Margin = Padding.Empty };
        notificationDuration = new NumericUpDown { Name = "NotificationDurationSeconds", Minimum = 0, Maximum = 3600,
            DecimalPlaces = 0, Increment = 1, Value = settings.NotificationDurationSeconds, Width = 120, Margin = Padding.Empty };
        durationRow.Controls.Add(notificationDuration);
        durationRow.Controls.Add(new Label { Name = "NotificationDurationUnit", Text = "秒 (0 = 非表示)", AutoSize = true, Margin = new Padding(8, 3, 0, 0) });
        Add(notifications, "表示時間", durationRow, 2);
        durationRow.Margin = new Padding(3, 0, 0, 0);
        playCaptureSound = Toggle(notifications, "効果音", "PlayCaptureSound", "完了時に短い音を鳴らす", settings.PlayCaptureSound, 3);
        var notificationInfo = new Label { Name = "NotificationInfo", Dock = DockStyle.Fill,
            Text = "画面共有への映り込みを抑えますが、共有アプリによっては除外できません。\n効果音はPC音声の録音・共有に含まれる場合があります。" };
        notifications.Controls.Add(notificationInfo, 0, 4); notifications.SetColumnSpan(notificationInfo, 2);
        var presetPage = new TabPage("プリセット") { BackColor = BackColor, Padding = new Padding(8) };
        presetEditor = new CapturePresetEditor(settings, ReadSettings);
        presetPage.Controls.Add(presetEditor); tabs.TabPages.Add(presetPage);
        status = new TextBox { Name = "Status", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = BackColor, TabStop = false, Margin = new Padding(8, 6, 8, 6) };
        layout.Controls.Add(status, 0, 1);
        statusTimer.Tick += (_, _) => ExpireStatus();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var saveButton = MakeButton("保存", "SaveSettings", ButtonRole.Primary);
        saveButton.Click += (_, _) => SaveSettings();
        var closeButton = MakeButton("閉じる", "CloseSettings", ButtonRole.Neutral);
        closeButton.Click += (_, _) => Close();
        var folderButton = MakeButton("保存先を開く", "OpenFolder", ButtonRole.Secondary);
        folderButton.Width = 144;
        folderButton.Click += (_, _) => openFolder();
        buttons.Controls.AddRange([saveButton, closeButton, folderButton]);
        layout.Controls.Add(buttons, 0, 2);
        AcceptButton = saveButton;
        CancelButton = closeButton;
        if (deviceError is not null) SetStatus(deviceError, true);
        SettingsHelp.Apply(this, settingTips);
    }

    public void SetStatus(string text, bool error = false) => SetStatus(new StatusMessage(text, error, now()));

    public void SetStatus(StatusMessage? message)
    {
        statusTimer.Stop();
        statusMessage = message is not null && !message.IsExpired(now()) ? message : null;
        status.Text = statusMessage?.DisplayText.ReplaceLineEndings("\r\n") ?? "";
        status.ForeColor = statusMessage?.IsError == true ? Color.FromArgb(166, 35, 35) : Color.FromArgb(50, 74, 82);
        status.SelectionStart = 0;
        status.SelectionLength = 0;
        status.ScrollToCaret();
        if (statusMessage is not null) statusTimer.Start();
    }

    private void ExpireStatus()
    {
        if (statusMessage?.IsExpired(now()) == true) SetStatus((StatusMessage?)null);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (status is not null && Visible) ExpireStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { statusTimer.Dispose(); settingTips.Dispose(); }
        base.Dispose(disposing);
    }
    private void SaveSettings()
    {
        try
        {
            var next = ReadSettings();
            string? error = save(next);
            SetStatus(error ?? "設定を保存しました。", error is not null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { SetStatus("保存できませんでした: " + ex.Message, true); }
    }
    private AppSettings ReadSettings() => initial with
            {
                SaveDirectory = Expand(directory.Text), DefaultMode = ((ModeItem)mode.SelectedItem!).Value,
                OpenFolderAfterSave = openFolderAfterSave.Checked,
                FileNamePattern = fileNamePattern.Text, OcrSeparator = ocrSeparator.Text, OneOcrDirectory = Expand(oneOcrDirectory.Text),
                DefaultDestination = (clipboardOutput.Checked ? OutputDestination.Clipboard : 0) | (fileOutput.Checked ? OutputDestination.File : 0) | (externalOutput.Checked ? OutputDestination.ExternalApp : 0),
                FreezeFrame = freeze.Checked, DimCaptureBackground = dimCaptureBackground.Checked, LimitToStartingMonitor = limitToStartingMonitor.Checked,
                ScreenshotPaddingPixels = (int)screenshotPadding.Value,
                ScreenshotColorDepth = (ScreenshotColorDepth)screenshotColorDepth.SelectedIndex,
                CapturePresets = presetEditor.Presets,
                ScreenshotShortcut1 = screenshot1.Value, ScreenshotShortcut2 = screenshot2.Value,
                RecordingShortcut1 = recording1.Value, RecordingShortcut2 = recording2.Value,
                LastRegionShortcut = lastRegionKey.Value, FixedWidth = (int)fixedWidth.Value, FixedHeight = (int)fixedHeight.Value,
                FixedCaptureAnchor = ((CaptureAnchorItem)fixedCaptureAnchor.SelectedItem!).Value,
                IncludeCursor = includeCursor.Checked, KeepHistory = keepHistory.Checked,
                NotificationDurationSeconds = (int)notificationDuration.Value, PlayCaptureSound = playCaptureSound.Checked,
                NotificationDisplay = ((NotificationDisplayItem)notificationDisplay.SelectedItem!).Value,
                NotificationCorner = ((CornerItem)notificationCorner.SelectedItem!).Value,
                InstantShortcuts = instantKeys.Select(key => key.Value).ToArray(),
                RecordSystemAudio = systemAudio.Checked, RecordMicrophone = micAudio.Checked,
                MicrophoneDeviceId = ((MicrophoneInfo)microphone.SelectedItem!).Id,
                ImageEditorPath = Expand(imageEditor.Text), VideoEditorPath = Expand(videoEditor.Text),
                ImageEditorArguments = imageArguments.Text, VideoEditorArguments = videoArguments.Text
            };
    private static string Expand(string text) => Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
    private static NumericUpDown PixelField(TableLayoutPanel table, string label, string name, int value, int row)
    { var control = new NumericUpDown { Name = name, Minimum = 1, Maximum = 32768, Value = value }; Add(table, label, control, row); return control; }
    private static HotkeyEditor KeyField(TableLayoutPanel table, string label, string name, string value, int row)
    { var control = new HotkeyEditor(name, value); Add(table, label, control, row); control.Margin = new Padding(3, 0, 0, 0); return control; }

    private (TextBox Path, TextBox Arguments) EditorPage(TabControl tabs, string title, string prefix,
        string path, string arguments, IReadOnlyList<EditorPreset> presets)
    {
        var page = Page(tabs, title);
        var preset = Combo(page, "サンプル", prefix + "EditorPreset", 0);
        foreach (var item in presets) preset.Items.Add(item);
        preset.SelectedIndex = 0;
        var executable = Field(page, "実行ファイル", prefix + "EditorPath", path, 1);
        executable.PlaceholderText = "C:\\...\\editor.exe";
        var parameters = Field(page, "パラメータ", prefix + "EditorArguments", arguments, 2);
        var help = new Label { AutoSize = false, Text = "{file} 完全パス　{directory} フォルダ\n{filename} ファイル名　パスは \"{file}\" と指定", Dock = DockStyle.Fill };
        page.Controls.Add(help, 0, 3);
        page.SetColumnSpan(help, 2);
        page.SetRowSpan(help, 2);
        preset.SelectedIndexChanged += (_, _) =>
        {
            if (preset.SelectedItem is not EditorPreset item || string.IsNullOrEmpty(item.Executable)) return;
            executable.Text = item.Executable;
            parameters.Text = item.Arguments;
            SetStatus(File.Exists(item.Executable) ? "サンプルを入力しました。" : "アプリが見つかりません。インストール先に合わせて実行ファイルを変更してください。");
        };
        return (executable, parameters);
    }
    private static TableLayoutPanel Page(TabControl tabs, string title)
    {
        var page = new TabPage(title) { BackColor = Color.FromArgb(247, 249, 251), Padding = new Padding(8) };
        tabs.TabPages.Add(page);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 5; row++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        return table;
    }
    private static void Add(TableLayoutPanel table, string label, Control control, int row)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3, 4, 0, 3);
        table.Controls.Add(control, 1, row);
    }
    private static TextBox Field(TableLayoutPanel table, string label, string name, string value, int row)
    { var control = new TextBox { Name = name, Text = value }; Add(table, label, control, row); return control; }
    private static ComboBox Combo(TableLayoutPanel table, string label, string name, int row)
    { var control = new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList }; Add(table, label, control, row); return control; }
    private static CheckBox Toggle(TableLayoutPanel table, string label, string name, string text, bool value, int row)
    { var control = new CheckBox { Name = name, Text = text, Checked = value, AutoSize = true }; Add(table, label, control, row); return control; }
    internal static Button MakeButton(string text, string name, ButtonRole role = ButtonRole.Secondary)
    {
        var button = new Button { Text = text, Name = name, Size = new Size(96, 36), Margin = new Padding(8, 0, 0, 0) };
        ButtonTheme.Apply(button, role);
        return button;
    }
    public static string ModeName(CaptureMode value) => value switch
    {
        CaptureMode.Rectangle => "矩形", CaptureMode.Window => "ウィンドウ",
        CaptureMode.FullScreen => "全画面", CaptureMode.Freeform => "自由形状", CaptureMode.FixedRegion => "固定サイズ", _ => "矩形"
    };
    private sealed record ModeItem(CaptureMode Value) { public override string ToString() => ModeName(Value); }
    private sealed record CornerItem(NotificationCorner Value) { public override string ToString() => Value == Services.NotificationCorner.BottomLeft ? "左下" : "右下"; }
    private sealed record CaptureAnchorItem(CaptureAnchor Value)
    {
        public override string ToString() => Value switch
        {
            CaptureAnchor.TopLeft => "左上", CaptureAnchor.TopRight => "右上",
            CaptureAnchor.BottomRight => "右下", CaptureAnchor.BottomLeft => "左下", _ => "左上"
        };
    }
    private sealed record NotificationDisplayItem(NotificationDisplay Value)
    {
        public override string ToString() => Value switch
        {
            Services.NotificationDisplay.Main => "メイン画面",
            Services.NotificationDisplay.Captured => "撮影した画面",
            _ => $"DISPLAY{(int)Value - (int)Services.NotificationDisplay.Display1 + 1}"
        };
    }
}
