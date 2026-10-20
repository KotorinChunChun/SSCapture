using SSCapture.Services;

namespace SSCapture.Capture;

internal sealed class CaptureContextMenu : ContextMenuStrip
{
    private readonly Action cancel;
    private readonly Font menuFont = new("Yu Gothic UI", 12F);
    private readonly Dictionary<OutputDestination, ToolStripMenuItem> destinations = [];
    private readonly Dictionary<CaptureMode, ToolStripMenuItem> modes = [];
    private readonly Dictionary<CaptureAspectRatio, ToolStripMenuItem> ratios = [];
    private readonly Dictionary<int, ToolStripMenuItem> paddings = [];
    private OutputDestination currentDestination;
    private readonly ToolStripMenuItem? gridItem;
    private readonly ToolStripMenuItem? guideItem;
    private readonly Func<bool> ownsPopupFocus;

    internal CaptureContextMenu(OutputDestination selected, Action<OutputDestination> changeDestination,
        Action openSettings, Action cancel, CaptureMode mode = CaptureMode.Rectangle, bool grid20 = false,
        Action<CaptureMode>? changeMode = null, Action<bool>? changeGrid = null, Action? captureDesktop = null,
        Func<bool>? ownsPopupFocus = null, bool showGuide = true, Action<bool>? changeGuide = null, Action<CaptureAspectRatio>? changeRatio = null,
        int defaultPadding = 0, Action<int>? changePadding = null, IReadOnlyList<CapturePreset>? presets = null, Action<CapturePreset>? applyPreset = null,
        IReadOnlyList<ExternalApplication>? applications = null, string selectedApplication = "", Action<string>? changeApplication = null)
    {
        this.cancel = cancel;
        this.ownsPopupFocus = ownsPopupFocus ?? (() => CaptureNative.OwnsForegroundWindow() || CaptureNative.ForegroundTransition);
        Font = menuFont;
        foreach (OutputDestination destination in new[] { OutputDestination.Clipboard, OutputDestination.File, OutputDestination.ExternalApp })
        {
            string text = destination switch
            {
                OutputDestination.Clipboard => "クリップボード",
                OutputDestination.File => "ファイル保存",
                OutputDestination.ExternalApp => "外部アプリ",
                _ => throw new ArgumentOutOfRangeException(nameof(selected))
            };
            ToolStripMenuItem item = new(text) { Tag = destination };
            item.Click += (_, _) => { var next = currentDestination ^ destination; changeDestination(next); SetDestination(next); };
            destinations.Add(destination, item);
            Items.Add(item);
        }
        Items.Add(new ToolStripSeparator());
        if (changeMode is not null)
        {
            var modeMenu = new ToolStripMenuItem("撮影モード") { Name = "CaptureModeMenu", ToolTipText = "現在の撮影中だけモードを変更します。次回は設定画面の撮影モードから開始します。", DropDown = new SelectionModeDropDown(this.ownsPopupFocus) };
            foreach (CaptureMode value in Enum.GetValues<CaptureMode>())
            {
                var item = new ToolStripMenuItem(UI.SettingsForm.ModeName(value)) { Tag = value };
                item.Click += (_, _) => changeMode(value);
                modes.Add(value, item);
                modeMenu.DropDownItems.Add(item);
            }
            Items.Add(modeMenu);
            SetMode(mode);
        }
        if (captureDesktop is not null)
            Items.Add(new ToolStripMenuItem("デスクトップ全体 (中央クリック)", null, (_, _) => captureDesktop())
            { Name = "CaptureDesktop", ToolTipText = "対象モニタ1枚の全体を撮影します。開始画面への制限がONのときは開始モニタ、OFFのときは右クリック位置のモニタが対象です。" });
        if (changeGrid is not null)
        {
            Items.Add(new ToolStripSeparator());
            gridItem = new ToolStripMenuItem("20px単位") { Name = "Grid20", Checked = grid20,
                ToolTipText = "ONのときは矩形の幅・高さを20物理ピクセル単位で変更します。OFFのときもAltを押している間は有効です。Altの枠吸着を優先するため、吸着時は20の倍数以外になる場合があります。" };
            gridItem.Click += (_, _) => changeGrid(!gridItem.Checked);
            Items.Add(gridItem);
        }
        if (changeGuide is not null)
        {
            guideItem = new ToolStripMenuItem("操作ガイドを表示") { Name = "ShowSelectionGuide", Checked = showGuide,
                ToolTipText = "範囲選択の案内を表示します。表示設定は次回にも保持し、ドラッグ中にポインタが近づくと一時的に隠れます。" };
            guideItem.Click += (_, _) => changeGuide(!guideItem.Checked);
            Items.Add(guideItem);
        }
        if (changeMode is not null || captureDesktop is not null || changeGrid is not null || changeGuide is not null) Items.Add(new ToolStripSeparator());
        Items.Add("設定", null, (_, _) => openSettings());
        Items.Add("閉じる / キャンセル", null, (_, _) => cancel());
        SetDestination(selected);
        if (changeApplication is not null)
        {
            var appMenu = new ToolStripMenuItem("今回の外部アプリ") { Name = "ExternalApplicationMenu" };
            foreach (var app in applications ?? [])
            {
                string id = app.Id;
                appMenu.DropDownItems.Add(new ToolStripMenuItem(app.Name, null, (_, _) => { changeApplication(id); SetApplication(id); }) { Name = id });
            }
            if (appMenu.DropDownItems.Count == 0) appMenu.DropDownItems.Add(new ToolStripMenuItem("設定画面で登録してください") { Enabled = false });
            Items.Insert(3, appMenu); SetApplication(selectedApplication);
        }
        if (changeRatio is not null)
        {
            var ratioMenu = new ToolStripMenuItem("縦横比") { Name = "AspectRatioMenu" };
            foreach (var ratio in Enum.GetValues<CaptureAspectRatio>())
            {
                var item = new ToolStripMenuItem(AspectRatios.Label(ratio)) { Checked = ratio == CaptureAspectRatio.Free };
                item.Click += (_, _) => changeRatio(ratio);
                ratios.Add(ratio, item); ratioMenu.DropDownItems.Add(item);
            }
            Items.Insert(Math.Max(0, Items.Count - 2), ratioMenu);
        }
        if (changePadding is not null)
        {
            var paddingMenu = new ToolStripMenuItem("今回の余白") { Name = "ScreenshotPaddingMenu" };
            var reset = new ToolStripMenuItem($"既定 ({defaultPadding}px)", null, (_, _) => changePadding(defaultPadding)) { Name = "DefaultPadding" };
            paddingMenu.DropDownItems.Add(reset);
            foreach (int pixels in new[] { 0, 10, 20, 40 })
            {
                int value = pixels;
                var item = new ToolStripMenuItem(value == 0 ? "余白なし" : $"{value}px", null, (_, _) => changePadding(value));
                paddings.Add(value, item); paddingMenu.DropDownItems.Add(item);
            }
            Items.Insert(Math.Max(0, Items.Count - 2), paddingMenu);
            SetPadding(defaultPadding);
        }
        if (applyPreset is not null)
        {
            var presetMenu = new ToolStripMenuItem("出力プリセット") { Name = "CapturePresetMenu" };
            foreach (var preset in presets ?? [])
            {
                var capturedPreset = preset;
                presetMenu.DropDownItems.Add(new ToolStripMenuItem(preset.Name, null, (_, _) => applyPreset(capturedPreset)) { Name = preset.Id });
            }
            if (presetMenu.DropDownItems.Count == 0)
                presetMenu.DropDownItems.Add(new ToolStripMenuItem("設定画面で登録してください") { Enabled = false });
            Items.Insert(Math.Max(0, Items.Count - 2), presetMenu);
        }
    }

    internal void SetMode(CaptureMode mode)
    {
        foreach (var (value, item) in modes) item.Checked = value == mode;
    }

    internal void SetApplication(string id)
    {
        if (Items["ExternalApplicationMenu"] is ToolStripMenuItem menu)
            foreach (var item in menu.DropDownItems.OfType<ToolStripMenuItem>()) item.Checked = item.Name == id;
    }

    internal void SetGrid(bool enabled) { if (gridItem is not null) gridItem.Checked = enabled; }
    internal void SetGuide(bool enabled) { if (guideItem is not null) guideItem.Checked = enabled; }
    internal void SetRatio(CaptureAspectRatio ratio)
    {
        foreach (var pair in ratios) pair.Value.Checked = pair.Key == ratio;
        if (Items["AspectRatioMenu"] is ToolStripMenuItem item) item.Enabled = modes.Count == 0 || modes[CaptureMode.Rectangle].Checked;
    }
    internal void SetPadding(int pixels)
    {
        foreach (var pair in paddings) pair.Value.Checked = pair.Key == pixels;
        if (Items["ScreenshotPaddingMenu"] is ToolStripMenuItem parent) parent.Text = $"今回の余白 ({pixels}px)";
    }

    protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange
            && modes.Values.Any(item => item.Owner is ToolStripDropDown { Visible: true })
            && ownsPopupFocus()) e.Cancel = true;
        base.OnClosing(e);
    }

    private sealed class SelectionModeDropDown(Func<bool> ownsPopupFocus) : ToolStripDropDownMenu
    {
        protected override void OnClosing(ToolStripDropDownClosingEventArgs e)
        {
            // 子メニューへの前面切替中は、親子のメニューをまとめて閉じない。
            if (e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange
                && ownsPopupFocus()) e.Cancel = true;
            base.OnClosing(e);
        }
    }

    internal void SetDestination(OutputDestination selected)
    {
        currentDestination = selected;
        foreach ((OutputDestination destination, ToolStripMenuItem item) in destinations)
            item.Checked = selected.HasFlag(destination);
    }

    internal bool HandleCommandKey(Keys keyData)
    {
        if ((keyData & Keys.KeyCode) != Keys.Escape) return false;
        cancel();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message m, Keys keyData) =>
        HandleCommandKey(keyData) || base.ProcessCmdKey(ref m, keyData);

    protected override bool ProcessDialogKey(Keys keyData) =>
        HandleCommandKey(keyData) || base.ProcessDialogKey(keyData);

    protected override void Dispose(bool disposing)
    {
        if (disposing) menuFont.Dispose();
        base.Dispose(disposing);
    }
}
