using System.Reflection;
using SSCapture.Capture;
using SSCapture.Recording;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed partial class SettingsForm : Form
{
    private AppSettings committed;
    private readonly TextBox directory, imageEditor, videoEditor, imageArguments, videoArguments, fileNamePattern, ocrSeparator, oneOcrDirectory;
    private readonly HotkeyEditor lastRecipeKey;
    private readonly CaptureKeyEditor captureBeforeKeys, captureKeys;
    private readonly NumericUpDown fixedWidth, fixedHeight, notificationDuration;
    private readonly ComboBox screenshotColorDepth;
    private readonly ComboBox metadataMode;
    private readonly CheckBox dimCaptureBackground;
    private readonly CheckBox includeCursor, keepHistory, playCaptureSound, limitToStartingMonitor;
    private readonly ComboBox notificationCorner, notificationDisplay, fixedCaptureAnchor;
    private readonly ComboBox notificationApplication;
    private readonly CheckBox holdNotification;
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
    private readonly PresetCaptureShortcutEditor presetShortcutEditor;
    private readonly TabControl shortcutTabs;
    private readonly TabPage workShortcutPage;
    private readonly ExternalApplicationsEditor imageApps, videoApps;
    private readonly RegionManagerForm inputPresetEditor;
    private readonly ListBox categories;
    private readonly NumericUpDown snapDistance, gridStep;
    private readonly CheckBox gridEnabled, guideEnabled, pointerGuideEnabled;
    private readonly CheckBox magnifierEnabled, shareCapturePresentation;
    private readonly ComboBox guideDisplay, magnifierDotCount;
    private readonly NumericUpDown magnifierZoom;
    private readonly StartupSettingsControl startupSettings;
    private readonly CaptureCommandControl captureCommand;

    public SettingsForm(AppSettings settings, Func<AppSettings, string?> onSave, Action<string> openFolder,
        Func<IReadOnlyList<MicrophoneInfo>>? listMicrophones = null, Action? openRegions = null, Action? openHistory = null,
        Func<DateTimeOffset>? now = null, Func<IReadOnlyList<DisplayTarget>>? listDisplays = null,
        SavedRegion? lastRegion = null, Func<Size?, CaptureAnchor, Task<Rectangle?>>? selectInputRegion = null,
        Func<IReadOnlyList<DisplayLayout>>? regionDisplays = null, IStartupRegistration? startupRegistration = null,
        AppSettings? lastOutputSettings = null, Action<string>? exportSettings = null,
        Func<AppSettings, string?>? importSettings = null)
    {
        settings = settings.UpdateDefaultProjections();
        settings = settings with
        {
            CaptureBeforeKeys = settings.CaptureBeforeKeys.WithBeforeActivations(settings.ControlActivation, settings.WindowActivation)
        };
        committed = settings;
        save = onSave;
        this.now = now ?? (() => DateTimeOffset.Now);
        Text = WindowTitle.Create("設定");
        Name = "SettingsForm";
        MinimizeBox = false;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 12F);
        BackColor = Color.FromArgb(247, 249, 251);
        ForeColor = Color.FromArgb(28, 37, 47);
        ClientSize = new Size(1280, 860);
        MinimumSize = SizeFromClientSize(new Size(1200, 800));
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();
        var layout = new TableLayoutPanel { Name = "SettingsLayout", Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 0f, 96f, 38f }) layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        Controls.Add(layout);
        var tabs = new TabControl { Name = "SettingsTabs", Dock = DockStyle.Fill, Margin = Padding.Empty };
        var categoryBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        categoryBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158)); categoryBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        categoryBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        categories = new ListBox { Name = "SettingsCategories", Dock = DockStyle.Fill, IntegralHeight = false, ItemHeight = 34 };
        tabs.Appearance = TabAppearance.FlatButtons; tabs.SizeMode = TabSizeMode.Fixed; tabs.ItemSize = new Size(1, 1); tabs.TabStop = false;
        categoryBody.Controls.Add(categories, 0, 0); categoryBody.Controls.Add(tabs, 1, 0); layout.Controls.Add(categoryBody, 0, 0);
        var basic = Page(tabs, "基本");
        basic.RowStyles.Clear(); basic.RowCount = 9;
        for (int row = 0; row < 8; row++) basic.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        basic.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var inputs = Page(tabs, "入力");
        directory = Field(basic, "保存先", "SaveDirectory", settings.SaveDirectory, 0);
        mode = Combo(inputs, "撮影モード", "CaptureMode", 0);
        foreach (var value in Enum.GetValues<CaptureMode>()) mode.Items.Add(new ModeItem(value));
        mode.SelectedIndex = (int)settings.DefaultMode;
        var destinations = new FlowLayoutPanel { WrapContents = false, Margin = Padding.Empty };
        clipboardOutput = OutputToggle("クリップボード", "ClipboardOutput", OutputDestination.Clipboard);
        fileOutput = OutputToggle("ファイル保存", "FileOutput", OutputDestination.File);
        externalOutput = OutputToggle("外部アプリ", "ExternalOutput", OutputDestination.ExternalApp);
        destinations.Controls.AddRange([clipboardOutput, fileOutput, externalOutput]);
        Add(basic, "出力先", destinations, 1);
        CheckBox OutputToggle(string text, string name, OutputDestination value) => new()
        { Text = text, Name = name, AutoSize = true, Checked = settings.DefaultDestination.HasFlag(value), Margin = new Padding(0, 3, 12, 0) };
        freeze = Toggle(basic, "撮影タイミング", "FreezeFrame", "撮影開始時の画面を固定", settings.FreezeFrame, 2);
        openFolderAfterSave = Toggle(basic, "保存後", "OpenFolderAfterSave", "保存フォルダを開く", settings.OpenFolderAfterSave, 3);
        screenshotColorDepth = Combo(basic, "静止画の減色", "ScreenshotColorDepth", 6);
        screenshotColorDepth.Items.AddRange(["減色なし", "256色（8ビット）", "16色（4ビット）"]);
        screenshotColorDepth.SelectedIndex = (int)settings.ScreenshotColorDepth;
        var naming = Page(tabs, "ファイル名");
        fileNamePattern = Field(naming, "パターン", "FileNamePattern", settings.FileNamePattern, 0);
        ocrSeparator = Field(naming, "OCR区切り", "OcrSeparator", settings.OcrSeparator, 1);
        oneOcrDirectory = Field(naming, "OneOCRフォルダ", "OneOcrDirectory", settings.OneOcrDirectory, 2);
        oneOcrDirectory.PlaceholderText = "空欄でSnipping Toolから自動検出";
        var metadataPage = Page(tabs, "撮影情報");
        metadataMode = Combo(metadataPage, "保存形式", "MetadataMode", 0);
        metadataMode.Items.AddRange(["記録しない", "PNG内XMP", "別JSON", "PNG内XMPと別JSON"]);
        metadataMode.SelectedIndex = (int)settings.MetadataMode;
        var metadataHelp = new Label { Dock = DockStyle.Fill, Text = "静止画のファイル保存時だけ、位置・UIA・exe/class/titleを記録します。\nウィンドウ名やアプリのパスを含むため、画像を共有する前にご確認ください。\n値の本文・パスワードの名前・UI全体のツリーは採取しません。\n画像とUIAは同時刻ではありません。未取得時は理由を記録します。" };
        metadataPage.Controls.Add(metadataHelp, 0, 1); metadataPage.SetColumnSpan(metadataHelp, 2); metadataPage.SetRowSpan(metadataHelp, 4);
        var namingHelp = new Label { Dock = DockStyle.Fill, Text = "{yyyymmdd} 年月日　{hhmmss} 時分秒　{ocr} 文字列\n{region} 範囲名　{#} 1,2…　{##} 01,02…　動画は.mp4" };
        naming.Controls.Add(namingHelp, 0, 3); naming.SetColumnSpan(namingHelp, 2); naming.SetRowSpan(namingHelp, 2);
        var shortcutPage = new TabPage("ショートカット") { BackColor = BackColor, Padding = new Padding(8) };
        shortcutTabs = new TabControl { Name = "ShortcutGroups", Dock = DockStyle.Fill };
        shortcutPage.Controls.Add(shortcutTabs);
        tabs.TabPages.Add(shortcutPage);
        var keys = Page(shortcutTabs, "PC作業中");
        workShortcutPage = (TabPage)keys.Parent!;
        keys.RowCount = 1;
        keys.RowStyles.Clear();
        keys.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        presetShortcutEditor = new PresetCaptureShortcutEditor(settings.PresetCaptureShortcuts ?? [], SaveWorkingShortcuts);
        keys.Controls.Add(presetShortcutEditor, 0, 0);
        keys.SetColumnSpan(presetShortcutEditor, 2);
        captureBeforeKeys = new CaptureKeyEditor(settings.CaptureBeforeKeys, settings.CaptureMouse, beforeSelection: true);
        captureKeys = new CaptureKeyEditor(settings.CaptureKeys, settings.CaptureMouse, beforeSelection: false);
        var beforePage = CaptureShortcutPage(FirstPointTab, captureBeforeKeys,
            "撮影を開始してから、1点目を決めるまでに使うキーとクリックを設定します。");
        var dragPage = CaptureShortcutPage(SecondPointTab, captureKeys,
            "ドラッグ中や、2点クリックで1点目を指定した後に使うキーとクリックを設定します。");
        shortcutTabs.TabPages.AddRange([beforePage, dragPage]);
        var regions = inputs;
        fixedWidth = PixelField(regions, "固定幅 (px)", "FixedWidth", settings.FixedWidth, 1);
        fixedHeight = PixelField(regions, "固定高さ (px)", "FixedHeight", settings.FixedHeight, 2);
        fixedCaptureAnchor = Combo(regions, "固定サイズの基点", "FixedCaptureAnchor", 3);
        foreach (var anchor in Enum.GetValues<CaptureAnchor>()) fixedCaptureAnchor.Items.Add(new CaptureAnchorItem(anchor));
        fixedCaptureAnchor.SelectedIndex = (int)settings.FixedCaptureAnchor;
        includeCursor = Toggle(regions, "マウスカーソル", "IncludeCursor", "画像・動画に含める", settings.IncludeCursor, 4);
        var historyPage = Page(tabs, "履歴");
        keepHistory = Toggle(historyPage, "撮影履歴", "KeepHistory", "メモリーに保持する", settings.KeepHistory, 0);
        var historyButton = MakeButton("撮影履歴", "OpenHistory", ButtonRole.Secondary);
        historyButton.Width = 144; historyButton.Enabled = openHistory is not null;
        historyButton.Click += (_, _) => openHistory?.Invoke();
        Add(historyPage, "管理", historyButton, 1);
        var instant = Page(shortcutTabs, "即時撮影キー");
        var instantPage = (TabPage)instant.Parent!;
        shortcutTabs.TabPages.Remove(instantPage);
        shortcutTabs.TabPages.Insert(1, instantPage);
        ((TabPage)instant.Parent!).AutoScroll = true;
        instant.Dock = DockStyle.Top;
        instant.Height = 480;
        instant.ColumnStyles[0].Width = 196;
        instant.RowCount = 12;
        instant.RowStyles.Clear();
        for (int row = 0; row < instant.RowCount; row++) instant.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        lastRecipeKey = KeyField(instant, "直前の設定で再撮影", "LastRecipeShortcut", settings.LastRecipeShortcut, 0);
        var displaySummary = new Label { Name = "InstantDisplays", Dock = DockStyle.Fill, AutoEllipsis = true };
        instant.Controls.Add(displaySummary, 0, 1); instant.SetColumnSpan(displaySummary, 2);
        var displayLabels = new Label[10];
        for (int index = 0; index < 10; index++)
        {
            instantKeys[index] = KeyField(instant, $"画面{index + 1}", $"InstantShortcut{index + 1}", settings.InstantShortcuts[index], index + 2);
            displayLabels[index] = (Label)instant.GetControlFromPosition(0, index + 2)!;
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
        instantPage.VisibleChanged += (_, _) => { if (instantPage.Visible) RefreshInstantDisplays(); };
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
        var externalPage = new TabPage("外部アプリ") { BackColor = BackColor };
        var appTabs = new TabControl { Name = "ExternalApplicationTabs", Dock = DockStyle.Fill };
        externalPage.Controls.Add(appTabs); tabs.TabPages.Add(externalPage);
        (IReadOnlyList<CapturePreset> Presets, string NotificationId) ReadApplicationUsage() =>
            (presetEditor?.Presets ?? settings.CapturePresets, NotificationAppId());
        imageApps = new ExternalApplicationsEditor(CaptureIntent.Screenshot, settings.ImageEditors, settings.SelectedImageEditorId,
            settings.ImageEditorPath, settings.ImageEditorArguments, EditorPresets.Images, ReadApplicationUsage,
            (apps, id) => CommitExternalApplications(CaptureIntent.Screenshot, apps, id));
        videoApps = new ExternalApplicationsEditor(CaptureIntent.Recording, settings.VideoEditors, settings.SelectedVideoEditorId,
            settings.VideoEditorPath, settings.VideoEditorArguments, EditorPresets.Videos, ReadApplicationUsage,
            (apps, id) => CommitExternalApplications(CaptureIntent.Recording, apps, id));
        var imagePage = new TabPage("画像"); imagePage.Controls.Add(imageApps); appTabs.TabPages.Add(imagePage);
        var videoPage = new TabPage("動画"); videoPage.Controls.Add(videoApps); appTabs.TabPages.Add(videoPage);
        imageEditor = imageApps.Executable; imageArguments = imageApps.Arguments;
        videoEditor = videoApps.Executable; videoArguments = videoApps.Arguments;
        var notifications = Page(tabs, "通知");
        notifications.RowCount = 8;
        notifications.RowStyles.Clear();
        for (int row = 0; row < 7; row++) notifications.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
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
        holdNotification = Toggle(notifications, "表示時間の保持", "HoldNotificationWhileApproaching", "ポインタの接近・近接中は設定秒数を維持", settings.HoldNotificationWhileApproaching, 4);
        notificationApplication = Combo(notifications, "編集ボタンのアプリ", "NotificationApplication", 5);
        void RefreshNotificationApps()
        {
            string id = NotificationAppId();
            notificationApplication.Items.Clear();
            notificationApplication.Items.Add(new NotificationAppItem("", "撮影時の出力プリセットのアプリ"));
            foreach (var app in imageApps.Applications.Concat(videoApps.Applications).DistinctBy(a => a.Id))
                notificationApplication.Items.Add(new NotificationAppItem(app.Id, app.Name));
            notificationApplication.SelectedIndex = Enumerable.Range(0, notificationApplication.Items.Count)
                .FirstOrDefault(i => ((NotificationAppItem)notificationApplication.Items[i]!).Id == id);
            notificationApplication.Enabled = true;
        }
        notificationApplication.Items.Add(new NotificationAppItem(settings.NotificationApplicationId, "")); notificationApplication.SelectedIndex = 0;
        RefreshNotificationApps();
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == notifications.Parent) RefreshNotificationApps(); };
        var notificationInfo = new Label { Name = "NotificationInfo", Dock = DockStyle.Fill,
            Text = "通知本文はクリックを透過し、ボタンと小画像だけ操作できます。保存成功時はフォルダを開けます。\n編集アプリは個別指定、または撮影時の出力プリセットを使います。\n画面共有への映り込みを抑えますが、共有アプリによっては除外できません。\n効果音はPC音声の録音・共有に含まれる場合があります。" };
        notifications.Controls.Add(notificationInfo, 0, 6); notifications.SetColumnSpan(notificationInfo, 2); notifications.SetRowSpan(notificationInfo, 2);
        var presetPage = new TabPage("出力プリセット") { BackColor = BackColor, Padding = new Padding(8) };
        presetEditor = new CapturePresetEditor(settings, () => committed with { ImageEditors = imageApps.Applications, VideoEditors = videoApps.Applications }, id => ShowPresetShortcuts(outputId: id));
        presetEditor.PreviousOutput = lastOutputSettings;
        imageApps.SelectedChanged += id => presetEditor.SetDefaultApplication(true, id);
        videoApps.SelectedChanged += id => presetEditor.SetDefaultApplication(false, id);
        presetEditor.DefaultApplicationsChanged += (image, video) =>
        {
            imageApps.SelectApplication(image ?? ""); videoApps.SelectApplication(video ?? "");
        };
        notificationApplication.SelectedIndexChanged += (_, _) =>
        {
            imageApps.RefreshUsage();
            videoApps.RefreshUsage();
        };
        imageApps.RefreshUsage();
        videoApps.RefreshUsage();
        presetPage.Controls.Add(presetEditor); tabs.TabPages.Add(presetPage);
        var inputPresetPage = new TabPage("入力プリセット") { BackColor = BackColor, Padding = new Padding(4) };
        inputPresetEditor = new RegionManagerForm(settings.SavedRegions, lastRegion,
            size => PickInputRegion(size, settings.FixedCaptureAnchor), CommitInputs, regionDisplays,
            selectAnchoredRegion: PickInputRegion, embedded: true, profileSettings: settings, openPresetShortcuts: id => ShowPresetShortcuts(inputId: id));
        inputPresetPage.Controls.Add(inputPresetEditor); tabs.TabPages.Add(inputPresetPage); inputPresetEditor.Show();
        inputPresetEditor.AttachRecordingSettings(systemAudio, micAudio, microphone);
        workShortcutPage.VisibleChanged += (_, _) =>
        {
            if (workShortcutPage.Visible) RefreshPresetShortcutChoices();
        };
        RefreshPresetShortcutChoices();
        async Task<Rectangle?> PickInputRegion(Size? size, CaptureAnchor anchor)
        {
            if (selectInputRegion is not null) return await selectInputRegion(size, anchor);
            using var outcome = await CaptureSession.SelectAsync(CaptureOptions.ForInputSelection(CurrentSelectionSettings(), size, anchor));
            return outcome.Selection?.Bounds;
        }
        // 旧単独ページを廃止し、プリセットの編集欄だけを公開する。
        basic.Controls.Clear();
        basic.RowStyles.Clear(); basic.RowCount = 5;
        foreach (int height in new[] { 108, 120, 188, 84, 46 }) basic.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        basic.Dock = DockStyle.Top;
        basic.AutoSize = true;
        basic.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ((TabPage)basic.Parent!).AutoScroll = true;

        var selectionOptions = BasicFields(34, 34, 34);
        limitToStartingMonitor = BasicCheckBox("LimitToStartingMonitor", "開始したモニター内で選択する", settings.LimitToStartingMonitor);
        dimCaptureBackground = BasicCheckBox("DimCaptureBackground", "選択中モニターの背景を暗くする", settings.DimCaptureBackground);
        AddFullRow(selectionOptions, limitToStartingMonitor, 0);
        AddFullRow(selectionOptions, dimCaptureBackground, 1);
        shareCapturePresentation = BasicCheckBox("ShareCapturePresentation", "撮影操作を画面共有・配信に表示する", settings.ShareCapturePresentation);
        AddFullRow(selectionOptions, shareCapturePresentation, 2);
        Add(basic, "選択範囲", selectionOptions, 0);

        var precisionOptions = BasicFields(33, 40, 40);
        gridEnabled = BasicCheckBox("GridEnabled", "グリッドスナップを既定でON", settings.Grid20);
        AddFullRow(precisionOptions, gridEnabled, 0);
        gridStep = PixelField(precisionOptions, "グリッド間隔（px）", "GridStepPixels", settings.GridStepPixels, 1);
        snapDistance = PixelField(precisionOptions, "ウィンドウスナップ距離（px）", "SnapDistancePixels", settings.SnapDistancePixels, 2);
        Add(basic, "位置・寸法の補正", precisionOptions, 1);

        var guideOptions = BasicFields(34, 34, 34, 40, 40);
        guideEnabled = BasicCheckBox("ShowSelectionGuide", "画面右下に操作ガイドを表示する", settings.ShowSelectionGuide);
        pointerGuideEnabled = BasicCheckBox("ShowPointerGuide", "ポインタ横に操作ガイドを表示する", settings.ShowPointerGuide);
        magnifierEnabled = BasicCheckBox("ShowMagnifier", "拡大鏡を表示する", settings.ShowMagnifier);
        guideOptions.Controls.Add(guideEnabled, 0, 0);
        guideDisplay = new ComboBox { Name = "SelectionGuideDisplay", AccessibleName = "画面右下の操作ガイドの表示先",
            DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 2, 0, 2) };
        guideDisplay.Items.AddRange(["撮影モニター", "メイン画面"]);
        guideDisplay.SelectedIndex = (int)settings.SelectionGuideDisplay;
        guideOptions.Controls.Add(guideDisplay, 1, 0);
        AddFullRow(guideOptions, pointerGuideEnabled, 1);
        AddFullRow(guideOptions, magnifierEnabled, 2);
        magnifierDotCount = Combo(guideOptions, "拡大鏡の表示ドット数", "MagnifierDotCount", 3);
        for (int dots = CaptureMagnifier.MinDotCount; dots <= CaptureMagnifier.MaxDotCount; dots += 2)
            magnifierDotCount.Items.Add(dots);
        magnifierDotCount.FormattingEnabled = true;
        magnifierDotCount.Format += (_, e) => { if (e.ListItem is int dots) e.Value = $"{dots} × {dots}"; };
        magnifierDotCount.SelectedItem = settings.MagnifierDotCount;
        magnifierZoom = new NumericUpDown { Name = "MagnifierZoom", Minimum = CaptureMagnifier.MinZoom,
            Maximum = CaptureMagnifier.MaxZoom, Value = settings.MagnifierZoom };
        Add(guideOptions, "拡大鏡の拡大倍率（倍）", magnifierZoom, 4);
        Add(basic, "操作ガイド", guideOptions, 2);

        startupSettings = new StartupSettingsControl(startupRegistration ?? new StartupRegistration(), (int)basic.ColumnStyles[0].Width + 3);
        AddFullRow(basic, startupSettings, 3);
        var transfer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 3, 0, 3) };
        var exportButton = MakeButton("設定をエクスポート", "ExportSettings");
        var importButton = MakeButton("設定をインポート", "ImportSettings");
        exportButton.AutoSize = importButton.AutoSize = true;
        exportButton.Enabled = exportSettings is not null;
        importButton.Enabled = importSettings is not null;
        exportButton.Click += (_, _) =>
        {
            using var dialog = new SettingsTransferForm(exportSettings!);
            if (dialog.ShowDialog(this) == DialogResult.OK) SetStatus("設定を書き出しました。");
        };
        importButton.Click += (_, _) =>
        {
            using var dialog = new SettingsTransferForm(SettingsTransfer.Read, candidate => SettingsTransfer.Preview(
                committed, candidate, regionDisplays ?? RegionCoordinates.ReadDisplays,
                listMicrophones ?? RecordingService.GetMicrophones, listDisplays));
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Candidate is not { } candidate) return;
            if (importSettings!(candidate) is { } error) SetStatus(error, true);
        };
        transfer.Controls.AddRange([exportButton, importButton]);
        basic.Controls.Add(transfer, 1, 4);
        foreach (Label label in basic.Controls.OfType<Label>())
        {
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            label.Margin = new Padding(3, 7, 3, 0);
        }
        Shown += async (_, _) => await startupSettings.RefreshAsync();
        oneOcrDirectory.Parent!.Controls.Remove(oneOcrDirectory);
        var outputActions = presetEditor.AddSaveTools(oneOcrDirectory, openFolder);
        AddApplyButton(outputActions, oneOcrDirectory, () => committed with { OneOcrDirectory = Expand(oneOcrDirectory.Text) },
            () => oneOcrDirectory.Text = committed.OneOcrDirectory);
        foreach (var removed in new[] { (TabPage)inputs.Parent!, (TabPage)naming.Parent!, (TabPage)metadataPage.Parent!, (TabPage)audio.Parent! }) { tabs.TabPages.Remove(removed); removed.Dispose(); }
        directory = (TextBox)presetEditor.Controls.Find("SaveDirectory", true).Single(); fileNamePattern = (TextBox)presetEditor.Controls.Find("FileNamePattern", true).Single(); ocrSeparator = (TextBox)presetEditor.Controls.Find("OcrSeparator", true).Single();
        clipboardOutput = (CheckBox)presetEditor.Controls.Find("ClipboardOutput", true).Single(); fileOutput = (CheckBox)presetEditor.Controls.Find("FileOutput", true).Single(); externalOutput = (CheckBox)presetEditor.Controls.Find("ExternalOutput", true).Single();
        openFolderAfterSave = (CheckBox)presetEditor.Controls.Find("OpenFolderAfterSave", true).Single(); screenshotColorDepth = (ComboBox)presetEditor.Controls.Find("ScreenshotColorDepth", true).Single(); metadataMode = (ComboBox)presetEditor.Controls.Find("MetadataMode", true).Single();

        var commandPage = new TabPage("コマンド生成") { BackColor = BackColor, Padding = new Padding(8) };
        captureCommand = new CaptureCommandControl(() => committed, SetStatus);
        commandPage.Controls.Add(captureCommand); tabs.TabPages.Add(commandPage);
        AddVersionPage(tabs);
        string[] order = ["基本", "通知", "入力プリセット", "出力プリセット", "ショートカット", "外部アプリ", "コマンド生成", "履歴", "バージョン情報"];
        var ordered = order.Select(title => tabs.TabPages.Cast<TabPage>().Single(p => p.Text == title)).ToArray();
        tabs.TabPages.Clear(); tabs.TabPages.AddRange(ordered);
        foreach (var page in ordered)
        {
            if (page == shortcutPage)
            {
                foreach (TabPage group in shortcutTabs.TabPages) AddPageActions(group);
            }
            else if (page.Text != "バージョン情報") AddPageActions(page);
        }
        status = new TextBox { Name = "Status", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = BackColor, TabStop = false, Margin = new Padding(8, 6, 8, 6) };
        layout.Controls.Add(status, 0, 1);
        statusTimer.Tick += (_, _) => ExpireStatus();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        var closeButton = MakeButton("閉じる", "CloseSettings", ButtonRole.Neutral);
        closeButton.Click += (_, _) => Close();
        buttons.Controls.Add(closeButton);
        layout.Controls.Add(buttons, 0, 2);
        CancelButton = closeButton;
        if (deviceError is not null) SetStatus(deviceError, true);
        categories.DisplayMember = nameof(TabPage.Text); categories.Items.AddRange(tabs.TabPages.Cast<TabPage>().Cast<object>().ToArray()); categories.SelectedIndex = 0;
        categories.SelectedIndexChanged += (_, _) => { if (categories.SelectedIndex >= 0) tabs.SelectedIndex = categories.SelectedIndex; };
        tabs.SelectedIndexChanged += (_, _) => { if (categories.SelectedIndex != tabs.SelectedIndex) categories.SelectedIndex = tabs.SelectedIndex; };
        SettingsHelp.Apply(this, settingTips);
        BindPresetShortcutLinks();
        inputPresetEditor.EnableAutoSave();
        presetEditor.EnableAutoSave(CommitOutputs);
        FormClosing += (_, e) =>
        {
            if (SelectedCategory == "入力プリセット") e.Cancel = !inputPresetEditor.StoreCurrent();
            if (SelectedCategory == "出力プリセット")
            {
                presetEditor.StoreCurrent();
                e.Cancel = presetEditor.Error is not null;
            }
        };
        Shown += (_, _) => { var work = Screen.FromControl(this).WorkingArea; MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height)); if (Width > work.Width || Height > work.Height) Bounds = new Rectangle(work.Location, new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height))); };
    }

    public void SetStatus(string text, bool error = false) => SetStatus(new StatusMessage(text, error, now()));
    internal string? SelectedCategory => (categories.SelectedItem as TabPage)?.Text;

    internal void SelectCategory(string title)
    {
        for (int index = 0; index < categories.Items.Count; index++)
            if (((TabPage)categories.Items[index]!).Text == title) { categories.SelectedIndex = index; return; }
    }

    internal void ShowCaptureSettings(CaptureSettingsLink link)
    {
        switch (link.Destination)
        {
            case CaptureSettingsDestination.InputPreset:
                SelectCategory("入力プリセット");
                inputPresetEditor.SelectPreset(link.PresetId);
                break;
            case CaptureSettingsDestination.OutputPreset:
                SelectCategory("出力プリセット");
                presetEditor.SelectPreset(link.PresetId);
                break;
            case CaptureSettingsDestination.Shortcut:
                SelectCategory("ショートカット");
                Control target = link.ShortcutControlName is { Length: > 0 } name
                    ? Controls.Find(name, true).FirstOrDefault() ?? captureBeforeKeys
                    : captureBeforeKeys;
                for (Control? parent = target.Parent; parent is not null; parent = parent.Parent)
                    if (parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
                target.Select();
                break;
        }
    }

    internal void ShowPresetShortcuts(string? inputId = null, string? outputId = null)
    {
        RefreshPresetShortcutChoices();
        SelectCategory("ショートカット");
        shortcutTabs.SelectedTab = workShortcutPage;
        presetShortcutEditor.OpenForPreset(inputId, outputId);
    }

    internal void UpdateLastRegion(SavedRegion? region) => inputPresetEditor.UpdateLastRegion(region);

    private void RefreshPresetShortcutChoices() => presetShortcutEditor.RefreshPresets(inputPresetEditor.Regions, presetEditor.Presets);

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
    private void SaveWorkingShortcuts()
    {
        var error = CommitEditorSettings(committed with
        {
            PresetCaptureShortcuts = presetShortcutEditor.Rows
        });
        SetStatus(error ?? "PC作業中のショートカットを保存しました。", error is not null);
    }

    private string? CommitExternalApplications(CaptureIntent intent, ExternalApplication[] apps, string id) =>
        CommitEditorSettings(WithExternalApplications(committed, intent, apps, id));
    private string? CommitInputs(SavedRegion[] regions)
    {
        foreach (var removed in committed.SavedRegions.Where(old => regions.All(p => p.Id != old.Id)))
            if (PresetRemovalError(removed.Id, input: true) is { } error) return error;
        return CommitEditorSettings(committed with { SavedRegions = regions });
    }

    private string? CommitOutputs(CapturePreset[] presets)
    {
        foreach (var removed in committed.CapturePresets.Where(old => presets.All(p => p.Id != old.Id)))
            if (PresetRemovalError(removed.Id, input: false) is { } error) return error;
        return CommitEditorSettings(committed with { CapturePresets = presets });
    }

    private string? PresetRemovalError(string id, bool input) =>
        PresetCaptureShortcut.ReferenceError(presetShortcutEditor.Rows, id, input)
        ?? PresetCaptureShortcut.ReferenceError(committed.PresetCaptureShortcuts ?? [], id, input);
    private string? CommitEditorSettings(AppSettings next)
    {
        if (next == committed) return null;
        try
        {
            if (next.OneOcrDirectory != committed.OneOcrDirectory && next.OneOcrDirectory.Length != 0
                && (!Path.IsPathFullyQualified(next.OneOcrDirectory) || next.OneOcrDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0))
                return "OCRエンジンの場所はフォルダーの絶対パスで指定してください。";
            next = next.ApplyDefaults();
            next.Validate();
            string? error = save(next);
            if (error is null)
            {
                committed = next;
                refreshSettingControls?.Invoke();
            }
            return error;
        }
        catch (Exception ex) { return "保存できません: " + ex.Message; }
    }
    private static string Expand(string text) => Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
    private AppSettings CurrentSelectionSettings() => committed with
    {
        LimitToStartingMonitor = limitToStartingMonitor.Checked, DimCaptureBackground = dimCaptureBackground.Checked,
        Grid20 = gridEnabled.Checked, ShowSelectionGuide = guideEnabled.Checked, ShowPointerGuide = pointerGuideEnabled.Checked,
        SelectionGuideDisplay = (SelectionGuideDisplay)guideDisplay.SelectedIndex,
        ShowMagnifier = magnifierEnabled.Checked, ShareCapturePresentation = shareCapturePresentation.Checked,
        MagnifierDotCount = (int)magnifierDotCount.SelectedItem!, MagnifierZoom = SettingNumber(magnifierZoom),
        SnapDistancePixels = SettingNumber(snapDistance), GridStepPixels = SettingNumber(gridStep)
    };
    private string NotificationAppId() => (notificationApplication?.SelectedItem as NotificationAppItem)?.Id ?? committed.NotificationApplicationId;
    private sealed record NotificationAppItem(string Id, string Label) { public override string ToString() => Label; }
    private static NumericUpDown PixelField(TableLayoutPanel table, string label, string name, int value, int row)
    { var control = new NumericUpDown { Name = name, Minimum = 1, Maximum = 32768, Value = value }; Add(table, label, control, row); return control; }

    private static TableLayoutPanel BasicFields(params int[] heights)
    {
        var table = new TableLayoutPanel { ColumnCount = 2, RowCount = heights.Length };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in heights) table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        return table;
    }

    private static CheckBox BasicCheckBox(string name, string label, bool isChecked) =>
        new() { Name = name, Text = label, Checked = isChecked, AutoSize = true };

    private static void AddFullRow(TableLayoutPanel table, Control control, int row)
    {
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, table.ColumnCount);
    }
    internal void UpdateLastOutputSettings(AppSettings? value) => presetEditor.PreviousOutput = value;

    internal void UpdateCapturePreferences(AppSettings latest)
    {
        if (gridEnabled.Checked == committed.Grid20) gridEnabled.Checked = latest.Grid20;
        if (guideEnabled.Checked == committed.ShowSelectionGuide) guideEnabled.Checked = latest.ShowSelectionGuide;
        committed = committed with { Grid20 = latest.Grid20, ShowSelectionGuide = latest.ShowSelectionGuide };
        refreshSettingControls?.Invoke();
    }

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
    private const string FirstPointTab = "1点目";
    private const string SecondPointTab = "2点目";

    private void AddVersionPage(TabControl tabs)
    {
        const string projectUrl = "https://github.com/KotorinChunChun/SSCapture";
        var attributes = typeof(SettingsForm).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        string? ReadMetadata(string key) => attributes.FirstOrDefault(attribute => attribute.Key == key)?.Value;
        string? channel = ReadMetadata("ProductChannel");
        string version = WindowTitle.Create() + (string.IsNullOrWhiteSpace(channel) ? "" : $" ({channel})");

        var page = Page(tabs, "バージョン情報");
        AddInfo("バージョン", "ProductVersion", version, 0);
        AddInfo("更新日", "ProductUpdatedOn", ReadMetadata("ProductUpdatedOn") ?? "—", 1);
        AddInfo("作者", "ProductAuthor", "ことりちゅん", 2);
        AddInfo("概要", "ProductSummary", "本ツールは、高機能な汎用スクリーンショット撮影ソフトです", 3);

        var readme = new LinkLabel { Name = "GitHubReadme", Text = "使い方", TextAlign = ContentAlignment.MiddleLeft };
        readme.Links.Add(0, readme.Text.Length, projectUrl + "#readme");
        readme.LinkClicked += (_, e) => OpenLink((string)e.Link!.LinkData!);

        var links = new FlowLayoutPanel { WrapContents = false, Padding = Padding.Empty };
        readme.AutoSize = true;
        readme.Margin = new Padding(0, 6, 0, 0);
        links.Controls.AddRange([readme,
            LinkButton("アップデート情報", "GitHubReleases", projectUrl + "/releases"),
            LinkButton("不具合を報告", "GitHubIssues", projectUrl + "/issues")]);
        Add(page, "関連リンク", links, 4);
        page.RowStyles[4].Height = 46;

        void AddInfo(string label, string name, string value, int row) =>
            Add(page, label, new Label { Name = name, Text = value, TextAlign = ContentAlignment.MiddleLeft }, row);

        Button LinkButton(string text, string name, string url)
        {
            var button = MakeButton(text, name);
            button.Width = 152;
            button.Margin = new Padding(12, 0, 0, 0);
            button.Tag = url;
            button.Click += (_, _) => OpenLink(url);
            return button;
        }

        void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                SetStatus("リンクを開けませんでした。 " + ex.Message, true);
            }
        }
    }

    private TabPage CaptureShortcutPage(string title, CaptureKeyEditor editor, string description)
    {
        var page = new TabPage(title) { BackColor = BackColor, Padding = new Padding(8) };
        var explanation = new Label { Name = "ShortcutStageDescription", Text = description, AutoSize = true,
            Dock = DockStyle.Top, Padding = new Padding(3, 3, 3, 8) };
        page.Controls.Add(editor);
        page.Controls.Add(explanation);
        return page;
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
        CaptureMode.FullScreen => "全画面", CaptureMode.Freeform => "自由形状", CaptureMode.FixedRegion => "固定サイズ", CaptureMode.Scroll => "スクロール撮影", _ => "矩形"
    };
    private sealed record ModeItem(CaptureMode Value) { public override string ToString() => ModeName(Value); }
    private sealed record CornerItem(NotificationCorner Value)
    {
        public override string ToString() => Value switch
        { Services.NotificationCorner.TopLeft => "左上", Services.NotificationCorner.TopCenter => "上中央", Services.NotificationCorner.TopRight => "右上",
          Services.NotificationCorner.MiddleLeft => "左中央", Services.NotificationCorner.Center => "中央", Services.NotificationCorner.MiddleRight => "右中央",
          Services.NotificationCorner.BottomLeft => "左下", Services.NotificationCorner.BottomCenter => "下中央", _ => "右下" };
    }
    private sealed record CaptureAnchorItem(CaptureAnchor Value)
    {
        public override string ToString() => Value.Label();
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
