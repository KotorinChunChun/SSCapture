namespace SSCapture.Capture;

internal sealed class CaptureContextMenu : ContextMenuStrip
{
    private readonly Action cancel;
    private readonly Font menuFont = new("Yu Gothic UI", 12F);
    private readonly Dictionary<OutputDestination, ToolStripMenuItem> destinations = [];

    internal CaptureContextMenu(OutputDestination selected, Action<OutputDestination> changeDestination,
        Action openSettings, Action cancel)
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
        Items.Add("設定", null, (_, _) => openSettings());
        Items.Add("閉じる / キャンセル", null, (_, _) => cancel());
        SetDestination(selected);
    }

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
