using SSCapture.Capture;

namespace SSCapture.UI;

internal sealed class SelectionGuideForm : Form
{
    private readonly Action cancel;
    private readonly Func<Keys, bool>? handleKey;
    private readonly Action? modifiersChanged;
    private readonly Action? dismiss;
    private readonly Rectangle monitor;
    private readonly Rectangle workArea;
    private bool hiddenForPointer;
    private bool shared;
    private CaptureAnchor currentAnchor;
    private readonly CaptureMode mode;
    private readonly CaptureIntent intent;
    private readonly Size? fixedSize;
    private readonly CaptureKeyMap beforeKeys, dragKeys;
    private readonly CaptureMouseMap mouse;
    private readonly bool spaceEnabled;
    private readonly CaptureSelectionMethod selectionMethod;
    private readonly CaptureActivation controlActivation, windowActivation;
    private bool dragging, canCrossMonitors;
    private readonly Label help;
    internal SelectionGuideForm(Rectangle monitor, CaptureIntent intent, Action cancel, Size? fixedSize = null,
        Func<Keys, bool>? handleKey = null, CaptureAnchor fixedAnchor = CaptureAnchor.TopLeft, Action? modifiersChanged = null,
        CaptureMode mode = CaptureMode.Rectangle, Action? dismiss = null, bool spaceEnabled = true, Rectangle? workingArea = null,
        CaptureKeyMap? beforeKeys = null, CaptureKeyMap? dragKeys = null, CaptureMouseMap? mouse = null,
        bool canCrossMonitors = false, CaptureSelectionMethod selectionMethod = CaptureSelectionMethod.Drag,
        CaptureActivation controlActivation = CaptureActivation.Push, CaptureActivation windowActivation = CaptureActivation.Push)
    {
        this.cancel = cancel;
        this.handleKey = handleKey;
        this.modifiersChanged = modifiersChanged;
        this.dismiss = dismiss;
        this.monitor = monitor;
        Rectangle available = Rectangle.Intersect(monitor, workingArea ?? monitor);
        workArea = available.Width > 0 && available.Height > 0 ? available : monitor;
        currentAnchor = fixedAnchor;
        this.mode = fixedSize.HasValue ? CaptureMode.FixedRegion : mode;
        this.intent = intent;
        this.fixedSize = fixedSize;
        this.beforeKeys = beforeKeys ?? CaptureKeyMap.BeforeDefaults;
        this.dragKeys = dragKeys ?? CaptureKeyDefaults.Current.Drag;
        this.mouse = mouse ?? new CaptureMouseMap();
        this.spaceEnabled = spaceEnabled;
        this.selectionMethod = selectionMethod;
        this.controlActivation = controlActivation;
        this.windowActivation = windowActivation;
        this.canCrossMonitors = canCrossMonitors;
        Name = "SelectionGuide";
        Text = WindowTitle.Create(intent == CaptureIntent.Recording ? "録画範囲の選択" : "撮影範囲の選択");
        Font = new Font("Yu Gothic UI", 11F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        help = new Label { Name = "SelectionHelp", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        Controls.Add(help);
        UpdateText();
        PositionGuide();
    }

    protected override void OnShown(EventArgs e)
    {
        ResizeToText();
        PositionGuide();
        base.OnShown(e);
    }

    private void PositionGuide() => Location = new Point(Math.Max(workArea.Left, workArea.Right - Width - 16),
        Math.Max(workArea.Top, Math.Min(monitor.Bottom - 36, workArea.Bottom - 16) - Height));

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDisplayAffinity();
    }

    internal void SetShared(bool value)
    {
        shared = value;
        if (IsHandleCreated) ApplyDisplayAffinity();
    }

    private void ApplyDisplayAffinity() =>
        _ = CaptureNative.SetWindowDisplayAffinity(Handle, shared ? 0 : CaptureNative.ExcludeFromCapture);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            if (dismiss is not null) dismiss(); else Hide();
        }
        base.OnFormClosing(e);
    }

    internal static bool ShouldHideForPointer(Rectangle bounds, Point pointer, bool dragging, int dpi, bool hidden)
    {
        if (!dragging) return false;
        int margin = (int)Math.Ceiling((hidden ? 40 : 24) * Math.Max(96, dpi) / 96.0);
        bounds.Inflate(margin, margin);
        return bounds.Contains(pointer);
    }

    internal void UpdatePointer(Point pointer, bool dragging)
    {
        if (IsDisposed || Disposing) return;
        bool hide = ShouldHideForPointer(Bounds, pointer, dragging, DeviceDpi, hiddenForPointer);
        if (hide == hiddenForPointer) return;
        hiddenForPointer = hide;
        if (hide) Hide(); else Show();
    }

    internal void ShowSaveError(string message)
    {
        help.Text = "案内の表示設定を保存できませんでした。撮影は継続できます。\n" + message;
        ResizeToText();
    }

    internal void UpdateOperations(bool dragPhase, bool crossMonitors)
    {
        if (dragging == dragPhase && canCrossMonitors == crossMonitors) return;
        dragging = dragPhase;
        canCrossMonitors = crossMonitors;
        UpdateText();
    }

    internal void UpdateAnchor(CaptureAnchor anchor)
    {
        currentAnchor = anchor;
        UpdateText();
    }

    private void UpdateText()
    {
        help.Text = SelectionGuideText.Create(mode, intent, fixedSize, currentAnchor,
            dragging ? dragKeys : beforeKeys, mouse, dragging, spaceEnabled, canCrossMonitors, selectionMethod, controlActivation, windowActivation, dragKeys.SpaceCycle);
        ResizeToText();
    }

    private void ResizeToText()
    {
        int frameWidth = Width - ClientSize.Width;
        int frameHeight = Height - ClientSize.Height;
        int width = Math.Min(LogicalToDeviceUnits(720), Math.Max(1, workArea.Width - frameWidth - 32));
        var textArea = new Size(Math.Max(1, width - help.Padding.Horizontal), int.MaxValue);
        int preferredHeight = TextRenderer.MeasureText(help.Text, help.Font, textArea, TextFormatFlags.WordBreak).Height
            + help.Padding.Vertical + LogicalToDeviceUnits(8);
        ClientSize = new Size(width, Math.Min(preferredHeight, Math.Max(1, workArea.Height - frameHeight - 52)));
        if (IsHandleCreated && workArea.Contains(Location))
            Location = new Point(Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width)),
                Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height)));
    }
    protected override bool ShowWithoutActivation => true;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Space && (msg.LParam.ToInt64() & (1L << 30)) != 0) return true;
        if (handleKey?.Invoke(keyData) == true) return true;
        if (keyData == Keys.Escape) { cancel(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void WndProc(ref Message m)
    {
        if (SSCapture.Capture.CaptureNative.IsModifierTransition(m.Msg, m.WParam))
        {
            modifiersChanged?.Invoke();
            return;
        }
        if (m.Msg == SSCapture.Capture.CaptureNative.WmSysCommand && (m.WParam.ToInt64() & 0xfff0) == SSCapture.Capture.CaptureNative.ScKeyMenu) return;
        base.WndProc(ref m);
        if (m.Msg == 0x001C && m.WParam == 0 && Visible && !Disposing) cancel();
    }

}
