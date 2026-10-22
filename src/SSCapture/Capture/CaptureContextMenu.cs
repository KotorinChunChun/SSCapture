using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed record CaptureMenuEditing(AppSettings Settings, Size Size, CaptureAnchor Anchor, bool Limited,
    bool Screenshot, bool CanRefresh, Action<Size> ChangeSize, Action<CaptureAnchor> ChangeAnchor,
    Action<bool> ChangeLimit, Action<ScreenshotColorDepth> ChangeColor, Action<CaptureMetadataMode> ChangeMetadata,
    Action<bool> ChangeDim, Action<bool> ChangeFreeze, Action<bool> ChangeCursor,
    Action<int, int>? ChangePrecision = null, Action<string, string, string>? ChangeOutputDetails = null,
    Action<bool>? ChangeOpenFolder = null, Action? ResetInput = null, Action? ResetOutput = null);

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
    private readonly Func<bool> ownsPopupFocus;
    private OutputDestination currentDestination;
    private UI.FixedSizeMenuEditor? sizeEditor;
    private UI.CapturePrecisionEditor? precisionEditor;
    private UI.CaptureOutputMenuEditor? outputEditor;

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
        // ToolStripControlHostから閉じた場合も、独立した子ポップアップを画面へ残さない。
        foreach (var command in AllCommands.Reverse().Where(i => i.HasDropDownItems && i.DropDown.Visible).ToArray())
            command.DropDown.Close(ToolStripDropDownCloseReason.ItemClicked);
        Close(ToolStripDropDownCloseReason.ItemClicked);
    }

    internal CaptureContextMenu(OutputDestination selected, Action<OutputDestination> changeDestination,
        Action openSettings, Action cancel, CaptureMode mode = CaptureMode.Rectangle, bool grid20 = false,
        Action<CaptureMode>? changeMode = null, Action<bool>? changeGrid = null, Action? captureDesktop = null,
        Func<bool>? ownsPopupFocus = null, bool showGuide = true, Action<bool>? changeGuide = null, Action<CaptureAspectRatio>? changeRatio = null,
        int defaultPadding = 0, Action<int>? changePadding = null, IReadOnlyList<CapturePreset>? presets = null, Action<CapturePreset>? applyPreset = null,
        IReadOnlyList<ExternalApplication>? applications = null, string selectedApplication = "", Action<string>? changeApplication = null,
        IReadOnlyList<SavedRegion>? inputPresets = null, Action<SavedRegion>? applyInputPreset = null, CaptureMenuEditing? editing = null)
    {
        this.cancel = cancel;
        this.ownsPopupFocus = ownsPopupFocus ?? (() => CaptureNative.OwnsForegroundWindow() || CaptureNative.ForegroundTransition);
        Font = menuFont;
        var range = Menu("入力", "CaptureRangeGroup", Items);
        var output = Menu("出力", "CaptureOutputGroup", Items);
        var operation = Menu("表示・操作", "CaptureOperationGroup", Items);
        if (changeMode is not null)
        {
            var parent = Menu("撮影モード", "CaptureModeMenu", range.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureMode>())
            {
                var item = Item(UI.SettingsForm.ModeName(value), "Mode" + value, () => changeMode(value), parent.DropDownItems);
                item.Tag = value; modes[value] = item;
            }
        }
        if (applyInputPreset is not null)
        {
            var parent = Menu("入力プリセット", "InputPresetMenu", range.DropDownItems);
            foreach (var preset in inputPresets ?? []) Item(preset.Name, preset.Id, () => applyInputPreset(preset), parent.DropDownItems);
            EmptyHint(parent);
        }
        if (editing is not null)
        {
            if (editing.ResetInput is not null) Item("選択した入力プリセットに戻す", "ResetInputPreset", editing.ResetInput, range.DropDownItems);
            if (editing.ResetOutput is not null) Item("選択した出力プリセットに戻す", "ResetOutputPreset", editing.ResetOutput, output.DropDownItems);
            var parent = Menu("固定寸法", "FixedSizeMenu", range.DropDownItems);
            sizeEditor = new UI.FixedSizeMenuEditor(editing.Size, editing.ChangeSize);
            parent.DropDownItems.Add(new ToolStripControlHost(sizeEditor) { AutoSize = false, Size = sizeEditor.Size, Padding = Padding.Empty, Margin = Padding.Empty });
            parent = Menu("固定サイズの基点", "FixedAnchorMenu", range.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureAnchor>()) anchors[value] = Item(AnchorName(value), "Anchor" + value, () => editing.ChangeAnchor(value), parent.DropDownItems);
            Toggle("開始画面内だけで選択", "LimitToStartingMonitor", editing.Limited, editing.ChangeLimit, operation.DropDownItems,
                "今回だけモニタ制限を切り替えます。ONは現在のポインタがあるモニタを対象にします。");
        }
        if (changeRatio is not null)
        {
            var parent = Menu("縦横比", "AspectRatioMenu", range.DropDownItems);
            foreach (var value in Enum.GetValues<CaptureAspectRatio>()) ratios[value] = Item(AspectRatios.Label(value), "Ratio" + value, () => changeRatio(value), parent.DropDownItems);
        }
        if (captureDesktop is not null) Item("対象画面全体 (中央クリック)", "CaptureDesktop", captureDesktop, range.DropDownItems);
        foreach (var destination in new[] { OutputDestination.Clipboard, OutputDestination.File, OutputDestination.ExternalApp })
        {
            string text = destination switch { OutputDestination.Clipboard => "クリップボード", OutputDestination.File => "ファイル保存", _ => "外部アプリ" };
            var item = Item(text, "Destination" + destination, () => { var next = currentDestination ^ destination; changeDestination(next); SetDestination(next); }, output.DropDownItems);
            item.Tag = destination; destinations[destination] = item;
        }
        output.DropDownItems.Add(new ToolStripSeparator());
        if (applyPreset is not null)
        {
            var parent = Menu("出力プリセット", "CapturePresetMenu", output.DropDownItems);
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
            var parent = Menu("画像の余白", "ScreenshotPaddingMenu", range.DropDownItems);
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
            var cursor = Toggle("カーソルを含める", "IncludeCursor", editing.Settings.IncludeCursor, editing.ChangeCursor, range.DropDownItems, "切替時に画面を取り直します。録画ではカーソル描画も今回だけ変更します。");
            freeze.Enabled = cursor.Enabled = editing.CanRefresh;
            Toggle("背景を暗くする", "DimCaptureBackground", editing.Settings.DimCaptureBackground, editing.ChangeDim, operation.DropDownItems);
            if (editing.ChangeOpenFolder is not null) Toggle("保存後にフォルダを開く", "OpenFolderAfterSave", editing.Settings.OpenFolderAfterSave, editing.ChangeOpenFolder, output.DropDownItems);
            if (editing.ChangePrecision is not null)
            {
                var parent = Menu("吸着距離・刻み幅", "CapturePrecisionMenu", operation.DropDownItems);
                precisionEditor = new(editing.Settings.SnapDistancePixels, editing.Settings.GridStepPixels, editing.ChangePrecision);
                parent.DropDownItems.Add(new ToolStripControlHost(precisionEditor) { AutoSize = false, Size = precisionEditor.Size });
            }
            if (editing.ChangeOutputDetails is not null)
            {
                var parent = Menu("保存・命名・OCR", "OutputDetailsMenu", output.DropDownItems);
                outputEditor = new(editing.Settings, editing.ChangeOutputDetails);
                parent.DropDownItems.Add(new ToolStripControlHost(outputEditor) { AutoSize = false, Size = outputEditor.Size });
            }
        }
        if (changeGrid is not null) Toggle("指定刻み幅を使用", "Grid20", grid20, changeGrid, operation.DropDownItems, "表示設定を次回にも保存します。Altを押している間も有効です。");
        if (changeGuide is not null) Toggle("操作ガイドを表示", "ShowSelectionGuide", showGuide, changeGuide, operation.DropDownItems, "表示設定を次回にも保存します。ドラッグ中に近づくと一時的に隠れます。");
        Items.Add(new ToolStripSeparator());
        Item("設定", "OpenSettings", openSettings, Items);
        Item("撮影をキャンセル", "CancelCapture", cancel, Items);
        SetMode(mode); SetRatio(CaptureAspectRatio.Free); SetApplication(selectedApplication); SetDestination(selected);
        if (editing is not null) UpdateEditing(editing.Settings, editing.Size, editing.Anchor, editing.Limited);
    }
    private ToolStripMenuItem Menu(string text, string name, ToolStripItemCollection owner)
    { var item = new ToolStripMenuItem(text) { Name = name, DropDown = new SelectionModeDropDown(ownsPopupFocus) }; owner.Add(item); return item; }
    private static ToolStripMenuItem Item(string text, string name, Action action, ToolStripItemCollection owner)
    { var item = new ToolStripMenuItem(text) { Name = name }; item.Click += (_, _) => action(); owner.Add(item); return item; }
    private static ToolStripMenuItem Toggle(string text, string name, bool initial, Action<bool> change, ToolStripItemCollection owner, string? help = null)
    { ToolStripMenuItem? item = null; item = Item(text, name, () => change(!item!.Checked), owner); item.Checked = initial; item.ToolTipText = help; return item; }
    private static void EmptyHint(ToolStripMenuItem parent)
    { if (parent.DropDownItems.Count == 0) parent.DropDownItems.Add(new ToolStripMenuItem("設定画面で登録してください") { Enabled = false }); }
    private static string AnchorName(CaptureAnchor value) => value switch { CaptureAnchor.TopLeft => "左上", CaptureAnchor.TopRight => "右上", CaptureAnchor.BottomRight => "右下", _ => "左下" };
    internal void SetMode(CaptureMode mode)
    { foreach (var pair in modes) pair.Value.Checked = pair.Key == mode; if (AllCommands.Any(i => i.Name == "AspectRatioMenu")) Command("AspectRatioMenu").Enabled = mode == CaptureMode.Rectangle; }
    internal void SetApplication(string id)
    { var menu = AllCommands.FirstOrDefault(i => i.Name == "ExternalApplicationMenu"); if (menu is not null) foreach (var item in menu.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == id; }
    internal void SetGrid(bool enabled) { var item = AllCommands.FirstOrDefault(i => i.Name == "Grid20"); if (item is not null) item.Checked = enabled; }
    internal void SetGuide(bool enabled) { var item = AllCommands.FirstOrDefault(i => i.Name == "ShowSelectionGuide"); if (item is not null) item.Checked = enabled; }
    internal void SetRatio(CaptureAspectRatio ratio) { foreach (var pair in ratios) pair.Value.Checked = pair.Key == ratio; }
    internal void SetPadding(int pixels)
    { foreach (var pair in paddings) pair.Value.Checked = pair.Key == pixels; var parent = AllCommands.FirstOrDefault(i => i.Name == "ScreenshotPaddingMenu"); if (parent is not null) parent.Text = $"画像の余白（{pixels}px）"; }
    internal void SetDestination(OutputDestination selected)
    { currentDestination = selected; foreach (var pair in destinations) pair.Value.Checked = selected.HasFlag(pair.Key); var item = AllCommands.FirstOrDefault(i => i.Name == "MetadataModeMenu"); if (item is not null) item.Enabled = selected.HasFlag(OutputDestination.File); }
    internal void UpdateEditing(AppSettings settings, Size size, CaptureAnchor anchor, bool limited)
    {
        foreach (var pair in anchors) pair.Value.Checked = pair.Key == anchor;
        foreach (var pair in colors) pair.Value.Checked = pair.Key == settings.ScreenshotColorDepth;
        foreach (var pair in metadata) pair.Value.Checked = pair.Key == settings.MetadataMode;
        foreach (var item in AllCommands) item.Checked = item.Name switch { "FreezeFrame" => settings.FreezeFrame, "IncludeCursor" => settings.IncludeCursor, "DimCaptureBackground" => settings.DimCaptureBackground, "LimitToStartingMonitor" => limited, _ => item.Checked };
        var parent = AllCommands.FirstOrDefault(i => i.Name == "FixedSizeMenu"); if (parent is not null) parent.Text = $"固定寸法（{size.Width}×{size.Height}px）";
        sizeEditor?.SetSize(size);
        precisionEditor?.SetValues(settings.SnapDistancePixels, settings.GridStepPixels);
        outputEditor?.SetValues(settings);
        var precision = AllCommands.FirstOrDefault(i => i.Name == "CapturePrecisionMenu"); if (precision is not null) precision.Text = $"吸着距離 {settings.SnapDistancePixels}px / 刻み幅 {settings.GridStepPixels}px";
        var grid = AllCommands.FirstOrDefault(i => i.Name == "Grid20"); if (grid is not null) grid.Text = $"{settings.GridStepPixels}px単位";
        var folder = AllCommands.FirstOrDefault(i => i.Name == "OpenFolderAfterSave"); if (folder is not null) { folder.Checked = settings.OpenFolderAfterSave; folder.Enabled = currentDestination.HasFlag(OutputDestination.File); }
        var details = AllCommands.FirstOrDefault(i => i.Name == "OutputDetailsMenu"); if (details is not null) details.Enabled = currentDestination.HasFlag(OutputDestination.File);
    }
    internal void SetPresetStatus(string inputName, string outputName, bool inputChanged, bool outputChanged, string inputId, string outputId)
    {
        var input = AllCommands.FirstOrDefault(i => i.Name == "InputPresetMenu"); var output = AllCommands.FirstOrDefault(i => i.Name == "CapturePresetMenu");
        if (input is not null) { input.Text = "入力プリセット: " + inputName + (inputChanged ? "（変更あり）" : ""); foreach (var item in input.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == inputId; }
        if (output is not null) { output.Text = "出力プリセット: " + outputName + (outputChanged ? "（変更あり）" : ""); foreach (var item in output.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == outputId; }
    }
    protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
    { if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange && AllCommands.Any(i => i.HasDropDownItems && i.DropDown.Visible) && ownsPopupFocus()) e.Cancel = true; base.OnClosing(e); }
    private sealed class SelectionModeDropDown(Func<bool> ownsPopupFocus) : ToolStripDropDownMenu
    { protected override void OnClosing(ToolStripDropDownClosingEventArgs e) { if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange && ownsPopupFocus()) e.Cancel = true; base.OnClosing(e); } }
    internal bool HandleCommandKey(Keys keyData) { if ((keyData & Keys.KeyCode) != Keys.Escape) return false; cancel(); return true; }
    protected override bool ProcessCmdKey(ref Message m, Keys keyData) => HandleCommandKey(keyData) || base.ProcessCmdKey(ref m, keyData);
    protected override bool ProcessDialogKey(Keys keyData) => HandleCommandKey(keyData) || base.ProcessDialogKey(keyData);
    protected override void Dispose(bool disposing) { if (disposing) menuFont.Dispose(); base.Dispose(disposing); }
}
