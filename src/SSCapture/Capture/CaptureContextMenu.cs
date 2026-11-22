using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record CaptureMenuEditing(AppSettings Settings, Size Size, CaptureAnchor Anchor, bool Limited,
    bool Screenshot, bool CanRefresh, Action<Size> ChangeSize, Action<CaptureAnchor> ChangeAnchor,
    Action<bool> ChangeLimit, Action<ScreenshotColorDepth> ChangeColor, Action<CaptureMetadataMode> ChangeMetadata,
    Action<bool> ChangeDim, Action<bool> ChangeFreeze, Action<bool> ChangeCursor,
    Action<int, int>? ChangePrecision = null, Action<string, string, string>? ChangeOutputDetails = null,
    Action<bool>? ChangeOpenFolder = null, Action? ResetInput = null, Action? ResetOutput = null,
    Action<bool>? ChangePointerGuide = null, Action<bool>? ChangeSystemAudio = null, Action<bool>? ChangeMicrophone = null);

internal sealed class CaptureContextMenu : ContextMenuStrip
{
    private readonly Action cancel;
    private readonly Font menuFont = new("Yu Gothic UI", 12F);
    private readonly Dictionary<OutputDestination, ToolStripMenuItem> destinations = [];
    private readonly Dictionary<CaptureMode, ToolStripMenuItem> modes = [];
    private readonly Dictionary<CaptureAspectRatio, ToolStripMenuItem> ratios = [];
    private readonly Dictionary<int, ToolStripMenuItem> paddings = [];
    private readonly Dictionary<CaptureAnchor, ToolStripMenuItem> anchors = [];
    private readonly Dictionary<ScreenshotColorDepth, ToolStripMenuItem> colors = [];
    private readonly Dictionary<CaptureMetadataMode, ToolStripMenuItem> metadata = [];
    private readonly ToolStripMenuItem? windowSource, visibleWindow, isolatedWindow;
    private OutputDestination currentDestination;
    private bool dispatchingCommand;
    private CaptureMode currentMode;
    private ToolStripMenuItem? selectionMethod, selectDrag, selectTwoClicks, magnifier, sharePresentation, shortcut;
    private ToolStripMenuItem? windowCrop, presetCrop, noCrop;
    private string? shortcutControlName;

    internal event Action? CommandInvoking;

    internal IEnumerable<ToolStripMenuItem> AllCommands => Walk(Items);
    private static IEnumerable<ToolStripMenuItem> Walk(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            yield return item;
            foreach (var child in Walk(item.DropDownItems)) yield return child;
        }
    }
    internal ToolStripMenuItem Command(string name) => AllCommands.Single(i => i.Name == name);
    internal void CloseForCapture()
    {
        // ItemClicked on a child rolls up the root before the child's own close completes.
        // Close any surviving children first without starting another roll-up.
        foreach (var command in AllCommands.Reverse().Where(i => i.HasDropDownItems && i.DropDown.Visible).ToArray())
            command.DropDown.Close();
        if (Visible) Close(ToolStripDropDownCloseReason.ItemClicked);
    }

    internal CaptureContextMenu(OutputDestination selected, Action<OutputDestination> changeDestination,
        Action openSettings, Action cancel, CaptureMode mode = CaptureMode.Rectangle, bool grid20 = false,
        Action<CaptureMode>? changeMode = null, Action<bool>? changeGrid = null, Action? captureDesktop = null,
        bool showGuide = true, Action<bool>? changeGuide = null, Action<CaptureAspectRatio>? changeRatio = null,
        int defaultPadding = 0, Action<int>? changePadding = null, IReadOnlyList<CapturePreset>? presets = null, Action<CapturePreset>? applyPreset = null,
        IReadOnlyList<ExternalApplication>? applications = null, string selectedApplication = "", Action<string>? changeApplication = null,
        IReadOnlyList<SavedRegion>? inputPresets = null, Action<SavedRegion>? applyInputPreset = null, CaptureMenuEditing? editing = null,
        Action<bool>? changeWindowSource = null, Action<CaptureSettingsDestination, string?>? openSettingsLink = null, bool allowScroll = true)
    {
        this.cancel = cancel;
        Font = menuFont;
        ShowItemToolTips = true;
        var inputPresetsMenu = Menu("入力プリセット", "InputPresetMenu", Items);
        var outputPresetsMenu = Menu("出力プリセット", "CapturePresetMenu", Items);
        if (changeWindowSource is not null)
        {
            windowSource = Menu("ウィンドウの撮影方式", "WindowCaptureSource", Items);
            visibleWindow = Item("見えている画面を撮影", "VisibleWindowCapture", () => changeWindowSource(false), windowSource.DropDownItems);
            isolatedWindow = Item("対象ウィンドウだけ撮影（重なった窓を除く）", "IsolatedWindowCapture", () => changeWindowSource(true), windowSource.DropDownItems);
            SetWindowSource(mode, false);
        }
        Items.Add(new ToolStripSeparator());
        var range = Menu("今回だけ入力を変更", "CaptureRangeGroup", Items);
        var output = Menu("今回だけ出力を変更", "CaptureOutputGroup", Items);
        var operation = Menu("表示・操作", "CaptureOperationGroup", Items);
        if (changeMode is not null)
        {
            var parent = Menu("撮影モード", "CaptureModeMenu", range.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureMode>())
            {
                if (value == CaptureMode.Scroll && (!allowScroll || editing is { Screenshot: false })) continue;
                var item = Item(UI.SettingsForm.ModeName(value), "Mode" + value, () => changeMode(value), parent.DropDownItems);
                item.Tag = value; modes[value] = item;
            }
        }
        if (applyInputPreset is not null)
        {
            var parent = inputPresetsMenu;
            foreach (var preset in inputPresets ?? [])
                if (preset.Mode != CaptureMode.Scroll || allowScroll && editing is not { Screenshot: false })
                    Item(preset.Name, preset.Id, () => applyInputPreset(preset), parent.DropDownItems);
            EmptyHint(parent);
        }
        if (editing is not null)
        {
            if (editing.ResetInput is not null) Item("選択した入力プリセットに戻す", "ResetInputPreset", editing.ResetInput, range.DropDownItems);
            if (editing.ResetOutput is not null) Item("選択した出力プリセットに戻す", "ResetOutputPreset", editing.ResetOutput, output.DropDownItems);
            var parent = Menu("固定サイズの基点", "FixedAnchorMenu", range.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureAnchor>()) anchors[value] = Item(value.Label(), "Anchor" + value, () => editing.ChangeAnchor(value), parent.DropDownItems);
            Toggle("開始したモニター内で選択する", "LimitToStartingMonitor", editing.Limited, editing.ChangeLimit, operation.DropDownItems,
                "今回だけモニター制限を切り替えます。ONに戻すとメニューを開いた位置のモニター内で選択します。");
        }
        if (changeRatio is not null)
        {
            var parent = Menu("縦横比", "AspectRatioMenu", range.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureAspectRatio>()) ratios[value] = Item(AspectRatios.Label(value), "Ratio" + value, () => changeRatio(value), parent.DropDownItems);
        }
        if (captureDesktop is not null) Item("対象画面全体", "CaptureDesktop", captureDesktop, range.DropDownItems);
        foreach (var destination in new[] { OutputDestination.Clipboard, OutputDestination.File, OutputDestination.ExternalApp, OutputDestination.Pinned })
        {
            if (destination == OutputDestination.Pinned && editing is { Screenshot: false }) continue;
            string text = destination switch { OutputDestination.Clipboard => "クリップボードへコピー", OutputDestination.File => "ファイル保存", OutputDestination.Pinned => "ピン留め表示", _ => "外部アプリ" };
            var item = Item(text, "Destination" + destination, () => { var next = currentDestination ^ destination; changeDestination(next); SetDestination(next); }, output.DropDownItems);
            item.Tag = destination; destinations[destination] = item;
        }
        output.DropDownItems.Add(new ToolStripSeparator());
        if (applyPreset is not null)
        {
            var parent = outputPresetsMenu;
            foreach (var preset in presets ?? []) Item(preset.Name, preset.Id, () => applyPreset(preset), parent.DropDownItems);
            EmptyHint(parent);
        }
        if (changeApplication is not null)
        {
            var parent = Menu("今回の外部アプリ", "ExternalApplicationMenu", output.DropDownItems);
            foreach (var app in applications ?? []) Item(app.Name, app.Id, () => { changeApplication(app.Id); SetApplication(app.Id); }, parent.DropDownItems);
            EmptyHint(parent);
        }
        if (changePadding is not null)
        {
            var parent = Menu("白い余白", "ScreenshotPaddingMenu", output.DropDownItems);
            foreach (int pixels in new[] { 0, 10, 20, 50 }) paddings[pixels] = Item($"{pixels}px", "Padding" + pixels, () => changePadding(pixels), parent.DropDownItems);
            SetPadding(defaultPadding);
        }
        if (editing is { Screenshot: true })
        {
            var parent = Menu("静止画の減色", "ScreenshotColorDepthMenu", output.DropDownItems);
            foreach (var value in Enum.GetValues<ScreenshotColorDepth>()) colors[value] = Item(value switch { ScreenshotColorDepth.Indexed8 => "256色（8ビット）", ScreenshotColorDepth.Indexed4 => "16色（4ビット）", _ => "減色なし" }, "Color" + value, () => editing.ChangeColor(value), parent.DropDownItems);
            parent = Menu("撮影情報", "MetadataModeMenu", output.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureMetadataMode>()) metadata[value] = Item(value switch { CaptureMetadataMode.PngXmp => "PNG内XMP", CaptureMetadataMode.Json => "別JSON", CaptureMetadataMode.Both => "PNG内XMPと別JSON", _ => "記録しない" }, "Metadata" + value, () => editing.ChangeMetadata(value), parent.DropDownItems);
            parent.ToolTipText = "静止画のファイル保存時に適用します。途中で有効にすると画面を再取得します。";
        }
        if (editing is not null)
        {
            var freeze = Toggle("撮影開始時の画面を固定", "FreezeFrame", editing.Settings.FreezeFrame, editing.ChangeFreeze, range.DropDownItems, "切替時に画面を取り直します。ONはその画像、OFFは確定時の画像を出力します。");
            var cursor = Toggle("マウスカーソルを含める", "IncludeCursor", editing.Settings.IncludeCursor, editing.ChangeCursor, range.DropDownItems, "切替時に画面を取り直します。録画ではマウスカーソルの描画も今回だけ変更します。");
            freeze.Enabled = cursor.Enabled = editing.CanRefresh;
            Toggle("選択中モニターの背景を暗くする", "DimCaptureBackground", editing.Settings.DimCaptureBackground, editing.ChangeDim, operation.DropDownItems);
            if (editing.ChangePointerGuide is { } changePointerGuide)
                Toggle("ポインタ横に操作ガイドを表示する", "ShowPointerGuide", editing.Settings.ShowPointerGuide, changePointerGuide, operation.DropDownItems);
            if (editing is { Screenshot: false, ChangeSystemAudio: { } changeSystemAudio, ChangeMicrophone: { } changeMicrophone })
            {
                var audio = Menu("今回の録画音声", "RecordingAudio", range.DropDownItems);
                Toggle("PCの再生音を録音する", "RecordSystemAudio", editing.Settings.RecordSystemAudio, changeSystemAudio, audio.DropDownItems);
                Toggle("マイクを録音する", "RecordMicrophone", editing.Settings.RecordMicrophone, changeMicrophone, audio.DropDownItems);
            }
            if (editing.ChangeOpenFolder is not null) Toggle("保存後にフォルダを開く", "OpenFolderAfterSave", editing.Settings.OpenFolderAfterSave, editing.ChangeOpenFolder, output.DropDownItems);
        }
        if (changeGrid is not null) Toggle("グリッドスナップを既定でON", "Grid20", grid20, changeGrid, operation.DropDownItems, "グリッドスナップのON/OFFを切り替え、次回の撮影にも引き継ぎます。");
        if (changeGuide is not null) Toggle("画面右下に操作ガイドを表示する", "ShowSelectionGuide", showGuide, changeGuide, operation.DropDownItems, "表示設定を次回にも保存します。ドラッグ中に近づくと一時的に隠れます。");
        if (inputPresetsMenu.DropDownItems.Count == 0) EmptyHint(inputPresetsMenu);
        if (outputPresetsMenu.DropDownItems.Count == 0) EmptyHint(outputPresetsMenu);
        Items.Add(new ToolStripSeparator());
        if (openSettingsLink is null)
            Item("設定", "OpenSettings", openSettings, Items);
        else
        {
            var settings = Menu("設定", "CaptureSettingsMenu", Items);
            settings.ToolTipText = "撮影を終了して設定を開きます。";
            SettingsItem("設定を開く", "OpenSettings", openSettings);
            SettingsItem("選択中の入力プリセット", "OpenInputPresetSettings", () => openSettingsLink(CaptureSettingsDestination.InputPreset, null));
            SettingsItem("選択中の出力プリセット", "OpenOutputPresetSettings", () => openSettingsLink(CaptureSettingsDestination.OutputPreset, null));
            shortcut = SettingsItem("ショートカット", "OpenShortcutSettings", () => openSettingsLink(CaptureSettingsDestination.Shortcut, shortcutControlName));

            ToolStripMenuItem SettingsItem(string text, string name, Action action)
            {
                var item = Item(text, name, action, settings.DropDownItems);
                item.ToolTipText = settings.ToolTipText;
                return item;
            }
        }
        Item("撮影をキャンセル", "CancelCapture", cancel, Items);
        SetMode(mode); SetRatio(CaptureAspectRatio.Free); SetApplication(selectedApplication); SetDestination(selected);
        if (editing is not null) UpdateEditing(editing.Settings, editing.Size, editing.Anchor, editing.Limited);
    }
    private ToolStripMenuItem Menu(string text, string name, ToolStripItemCollection owner)
    {
        var item = new CaptureSubmenuItem(text, HandleCommandKey) { Name = name };
        item.DropDown.ShowItemToolTips = true;
        owner.Add(item);
        return item;
    }

    private ToolStripMenuItem Item(string text, string name, Action action, ToolStripItemCollection owner)
    {
        var item = new ToolStripMenuItem(text) { Name = name };
        item.Click += (_, _) => RunCommand(action);
        owner.Add(item);
        return item;
    }

    private void RunCommand(Action action)
    {
        if (dispatchingCommand || IsDisposed) return;
        dispatchingCommand = true;
        try
        {
            CloseForCapture();
            CommandInvoking?.Invoke();
            action();
        }
        finally { dispatchingCommand = false; }
    }
    private ToolStripMenuItem Toggle(string text, string name, bool initial, Action<bool> change, ToolStripItemCollection owner, string? help = null)
    { ToolStripMenuItem? item = null; item = Item(text, name, () => change(!item!.Checked), owner); item.Checked = initial; item.ToolTipText = help; return item; }
    private static void EmptyHint(ToolStripMenuItem parent)
    { if (parent.DropDownItems.Count == 0) parent.DropDownItems.Add(new ToolStripMenuItem("設定画面で登録してください") { Enabled = false }); }
    internal void SetMode(CaptureMode mode)
    {
        currentMode = mode;
        foreach (var pair in modes) pair.Value.Checked = pair.Key == mode;
        if (AllCommands.FirstOrDefault(i => i.Name == "AspectRatioMenu") is { } aspect)
            aspect.Enabled = mode is CaptureMode.Rectangle or CaptureMode.Scroll;
        if (selectionMethod is not null) selectionMethod.Available = mode is CaptureMode.Rectangle or CaptureMode.Scroll;
        if (windowCrop is not null) windowCrop.Available = mode is CaptureMode.Rectangle or CaptureMode.Window;
    }

    internal void AddRectangleSelectionMethods(Action selectByDrag, Action selectByTwoClicks)
    {
        selectionMethod = Menu("選択方法", "RectangleSelectionMethod", Command("CaptureRangeGroup").DropDownItems);
        selectionMethod.Available = currentMode is CaptureMode.Rectangle or CaptureMode.Scroll;
        selectDrag = Item("ドラッグ", "SelectionMethodDrag", selectByDrag, selectionMethod.DropDownItems);
        selectTwoClicks = Item("2点クリック", "SelectionMethodTwoClicks", selectByTwoClicks, selectionMethod.DropDownItems);
        SetRectangleSelectionMethod(false);
    }

    internal void SetRectangleSelectionMethod(bool twoClicks)
    {
        if (selectDrag is not null) selectDrag.Checked = !twoClicks;
        if (selectTwoClicks is not null) selectTwoClicks.Checked = twoClicks;
    }

    internal void AddPresentationOptions(bool showMagnifier, Action<bool> changeMagnifier,
        bool shareCapturePresentation, Action<bool> changeSharePresentation)
    {
        var operation = Command("CaptureOperationGroup").DropDownItems;
        magnifier = Toggle("拡大鏡を表示する", "ShowMagnifier", showMagnifier, changeMagnifier, operation);
        sharePresentation = Toggle("今回の撮影操作を画面共有・配信に表示する", "ShareCapturePresentation",
            shareCapturePresentation, changeSharePresentation, operation);
    }

    internal void SetPresentationOptions(bool showMagnifier, bool shareCapturePresentation)
    {
        if (magnifier is not null) magnifier.Checked = showMagnifier;
        if (sharePresentation is not null) sharePresentation.Checked = shareCapturePresentation;
    }

    internal void SetControlSelectionShortcut(string shortcutLabel, string controlName)
    {
        if (shortcut is null) return;
        shortcut.Text = $"コントロール選択キー（{shortcutLabel}）";
        shortcutControlName = controlName;
    }

    internal void AddWindowCropOptions(Action usePreset, Action disable)
    {
        windowCrop = Menu("選択したウィンドウの内側の切り抜き", "WindowCropOptions", Command("CaptureOutputGroup").DropDownItems);
        presetCrop = Item("プリセット値を使う", "WindowCropPreset", usePreset, windowCrop.DropDownItems);
        noCrop = Item("今回は切り抜かない", "WindowCropOff", disable, windowCrop.DropDownItems);
        windowCrop.Available = currentMode is CaptureMode.Rectangle or CaptureMode.Window;
    }

    internal void SetWindowCrop(WindowCrop current, WindowCrop preset)
    {
        if (windowCrop is null) return;
        windowCrop.Text = $"ウィンドウの切り抜き（上{current.Top}／下{current.Bottom}／左{current.Left}／右{current.Right}px）";
        presetCrop!.Checked = current == preset;
        noCrop!.Checked = current.IsEmpty && !preset.IsEmpty;
    }
    internal void SetWindowSource(CaptureMode mode, bool isolated)
    {
        if (windowSource is null) return;
        windowSource.Available = mode == CaptureMode.Window;
        visibleWindow!.Checked = !isolated;
        isolatedWindow!.Checked = isolated;
    }
    internal void SetApplication(string id)
    { var menu = AllCommands.FirstOrDefault(i => i.Name == "ExternalApplicationMenu"); if (menu is not null) foreach (var item in menu.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == id; }
    internal void SetGrid(bool enabled) { var item = AllCommands.FirstOrDefault(i => i.Name == "Grid20"); if (item is not null) item.Checked = enabled; }
    internal void SetGuide(bool enabled) { var item = AllCommands.FirstOrDefault(i => i.Name == "ShowSelectionGuide"); if (item is not null) item.Checked = enabled; }
    internal void SetRatio(CaptureAspectRatio ratio) { foreach (var pair in ratios) pair.Value.Checked = pair.Key == ratio; }
    internal void SetPadding(int pixels)
    { foreach (var pair in paddings) pair.Value.Checked = pair.Key == pixels; var parent = AllCommands.FirstOrDefault(i => i.Name == "ScreenshotPaddingMenu"); if (parent is not null) parent.Text = $"白い余白（{pixels}px）"; }
    internal void SetDestination(OutputDestination selected)
    { currentDestination = selected; foreach (var pair in destinations) pair.Value.Checked = selected.HasFlag(pair.Key); var item = AllCommands.FirstOrDefault(i => i.Name == "MetadataModeMenu"); if (item is not null) item.Enabled = selected.HasFlag(OutputDestination.File); }
    internal void UpdateEditing(AppSettings settings, Size size, CaptureAnchor anchor, bool limited)
    {
        foreach (var pair in anchors) pair.Value.Checked = pair.Key == anchor;
        foreach (var pair in colors) pair.Value.Checked = pair.Key == settings.ScreenshotColorDepth;
        foreach (var pair in metadata) pair.Value.Checked = pair.Key == settings.MetadataMode;
        foreach (var item in AllCommands)
            item.Checked = item.Name switch
            {
                "FreezeFrame" => settings.FreezeFrame,
                "IncludeCursor" => settings.IncludeCursor,
                "DimCaptureBackground" => settings.DimCaptureBackground,
                "LimitToStartingMonitor" => limited,
                "ShowPointerGuide" => settings.ShowPointerGuide,
                "RecordSystemAudio" => settings.RecordSystemAudio,
                "RecordMicrophone" => settings.RecordMicrophone,
                _ => item.Checked
            };
        var parent = AllCommands.FirstOrDefault(i => i.Name == "FixedSizeMenu"); if (parent is not null) parent.Text = $"固定寸法（{size.Width}×{size.Height}px）";
        var grid = AllCommands.FirstOrDefault(i => i.Name == "Grid20"); if (grid is not null) grid.Text = $"グリッドスナップを既定でON（{settings.GridStepPixels}px）";
        var folder = AllCommands.FirstOrDefault(i => i.Name == "OpenFolderAfterSave"); if (folder is not null) { folder.Checked = settings.OpenFolderAfterSave; folder.Enabled = currentDestination.HasFlag(OutputDestination.File); }
        var details = AllCommands.FirstOrDefault(i => i.Name == "OutputDetailsMenu"); if (details is not null) details.Enabled = currentDestination.HasFlag(OutputDestination.File);
    }
    internal void SetPresetStatus(string inputName, string outputName, bool inputChanged, bool outputChanged, string inputId, string outputId)
    {
        var input = AllCommands.FirstOrDefault(i => i.Name == "InputPresetMenu"); var output = AllCommands.FirstOrDefault(i => i.Name == "CapturePresetMenu");
        if (input is not null) { input.Text = "入力プリセット: " + inputName + (inputChanged ? "（変更あり）" : ""); foreach (var item in input.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == inputId; }
        if (output is not null) { output.Text = "出力プリセット: " + outputName + (outputChanged ? "（変更あり）" : ""); foreach (var item in output.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == outputId; }
    }
    internal bool HandleCommandKey(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) != Keys.Escape) return false;
        RunCommand(cancel);
        return true;
    }
    private sealed class CaptureSubmenuItem(string text, Func<Keys, bool> handleKey) : ToolStripMenuItem(text)
    {
        // Preserve WinForms' generated drop-down ownership and route Esc from every level.
        protected override ToolStripDropDown CreateDefaultDropDown() => new CaptureSubmenu(handleKey);
    }

    private sealed class CaptureSubmenu(Func<Keys, bool> handleKey) : ToolStripDropDownMenu
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _ = CaptureNative.SetWindowDisplayAffinity(Handle, CaptureNative.ExcludeFromCapture);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData) =>
            handleKey(keyData) || base.ProcessCmdKey(ref message, keyData);
        protected override bool ProcessDialogKey(Keys keyData) =>
            handleKey(keyData) || base.ProcessDialogKey(keyData);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = CaptureNative.SetWindowDisplayAffinity(Handle, CaptureNative.ExcludeFromCapture);
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keyData) => HandleCommandKey(keyData) || base.ProcessCmdKey(ref m, keyData);
    protected override bool ProcessDialogKey(Keys keyData) => HandleCommandKey(keyData) || base.ProcessDialogKey(keyData);
    protected override void Dispose(bool disposing) { if (disposing) menuFont.Dispose(); base.Dispose(disposing); }
}
