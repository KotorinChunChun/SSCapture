namespace SSCapture.Capture;

internal sealed class CaptureContextMenu : ContextMenuStrip
{
    private readonly Action cancel;
    private readonly Font menuFont = new("Yu Gothic UI", 12F);
    private readonly Dictionary<OutputDestination, ToolStripMenuItem> destinations = [];
    private readonly Dictionary<CaptureMode, ToolStripMenuItem> modes = [];
    private readonly ToolStripMenuItem? gridItem;

    internal CaptureContextMenu(OutputDestination selected, Action<OutputDestination> changeDestination,
        Action openSettings, Action cancel, CaptureMode mode = CaptureMode.Rectangle, bool grid20 = false,
        Action<CaptureMode>? changeMode = null, Action<bool>? changeGrid = null, Action? captureDesktop = null)
    {
        this.cancel = cancel;
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
            item.Click += (_, _) => { selected ^= destination; changeDestination(selected); SetDestination(selected); };
            destinations.Add(destination, item);
            Items.Add(item);
        }
        Items.Add(new ToolStripSeparator());
        if (changeMode is not null)
        {
            var modeMenu = new ToolStripMenuItem("撮影モード") { Name = "CaptureModeMenu", ToolTipText = "現在の撮影中だけモードを変更します。次回は設定画面の撮影モードから開始します。" };
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
        if (changeMode is not null || captureDesktop is not null || changeGrid is not null) Items.Add(new ToolStripSeparator());
        Items.Add("設定", null, (_, _) => openSettings());
        Items.Add("閉じる / キャンセル", null, (_, _) => cancel());
        SetDestination(selected);
    }

    internal void SetMode(CaptureMode mode)
    {
        foreach (var (value, item) in modes) item.Checked = value == mode;
    }

    internal void SetGrid(bool enabled) { if (gridItem is not null) gridItem.Checked = enabled; }

    private void SetDestination(OutputDestination selected)
    {
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
