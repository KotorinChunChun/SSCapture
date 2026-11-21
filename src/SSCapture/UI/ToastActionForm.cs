using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SSCapture.Services;

namespace SSCapture.UI;

// 本文のLAYERED/TRANSPARENT窓とは分離。操作とサムネイルだけが入力を受ける。
internal sealed class ToastActionForm : Form
{
    internal const int ActionExtendedStyle = 0x08000000 | 0x80 | 0x80000 | 0x8;
    private readonly Func<nint, CaptureProtection> protect;
    private readonly bool nativeShow;
    private readonly ActionButton folder, edit, retry;
    private readonly PictureBox preview;
    private readonly ToolTip tips = new() { ShowAlways = true };
    private readonly ContextMenuStrip editorMenu = new() { Name = "NotificationEditorMenu" };
    internal bool IsEditorMenuOpen => editorMenu.Visible;
    private nint protectedHandle;
    private NotificationActions? actions;
    private bool ready;
    private Font? actionFont;
    private bool previewHover;
    private Point? dragOrigin;
    private CancellationTokenSource? dragGesture;
    internal ToastActionForm(Func<nint, CaptureProtection> protect, bool nativeShow)
    {
        this.protect = protect; this.nativeShow = nativeShow;
        Name = "CaptureToastActions"; Text = WindowTitle.Create("完了操作");
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None; ControlBox = false;
        BackColor = ToastCardStyle.Surface;
        folder = Make("フォルダを開く", "NotificationOpenFolder");
        edit = Make("外部アプリで編集", "NotificationEdit");
        retry = Make("失敗した出力を再実行", "NotificationRetry");
        preview = new PictureBox { Name = "NotificationDragImage", SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(235, 241, 244), TabStop = false, AccessibleName = "撮影画像をドラッグ" };
        tips.SetToolTip(preview, "フォルダへドラッグして移動。Ctrlを押すとコピー。対応アプリへドロップして開きます。");
        Controls.AddRange([folder, edit, retry, preview]);
        editorMenu.Opening += (_, e) => e.Cancel = !protect(editorMenu.Handle).Success;
        preview.MouseUp += (_, e) =>
        {
            if (!ready || e.Button != MouseButtons.Right || editorMenu.Items.Count == 0) return;
            editorMenu.Show(preview, e.Location);
        };
        preview.MouseEnter += (_, _) => { previewHover = true; preview.Invalidate(); };
        preview.MouseLeave += (_, _) => { previewHover = false; preview.Invalidate(); };
        preview.Paint += (_, e) => PaintPreviewGrip(e.Graphics);
        preview.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !ready || actions?.Drag is null) return;
            CancelGesture();
            if (e.Clicks > 1 && actions.Edit is not null) return;
            dragGesture = new(); dragOrigin = e.Location;
        };
        preview.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !ready || actions?.Edit is not { } open) return;
            CancelGesture();
            InvokeAction(open);
        };
        preview.MouseUp += (_, _) => CancelGesture();
        preview.MouseCaptureChanged += (_, _) => { if (!preview.Capture) CancelGesture(); };
        preview.MouseMove += async (_, e) =>
        {
            if (dragOrigin is not Point origin || dragGesture is null || e.Button != MouseButtons.Left || !ready || actions?.Drag is not { } drag) return;
            if (!DragThresholdReached(origin, e.Location, SystemInformation.DragSize)) return;
            dragOrigin = null;
            await drag(preview, dragGesture.Token);
        };
        folder.Click += (_, _) => InvokeAction(actions?.OpenFolder);
        edit.Click += (_, _) => InvokeAction(actions?.Edit);
        retry.Click += (_, _) => InvokeAction(actions?.Retry);
    }
    internal static bool DragThresholdReached(Point origin, Point current, Size threshold) =>
        !new Rectangle(origin.X - threshold.Width / 2, origin.Y - threshold.Height / 2, threshold.Width, threshold.Height).Contains(current);
    private void CancelGesture()
    {
        dragOrigin = null; dragGesture?.Cancel(); dragGesture?.Dispose(); dragGesture = null;
    }
    private ActionButton Make(string text, string name)
    {
        var button = new ActionButton { Text = text, Name = name, FlatStyle = FlatStyle.Flat,
            ForeColor = ToastCardStyle.Accent, TabStop = false, UseMnemonic = false };
        tips.SetToolTip(button, text);
        return button;
    }

    private void PaintPreviewGrip(Graphics graphics)
    {
        if (actions?.Drag is null || preview.Image is null) return;
        int unit = Math.Max(1, (int)Math.Round(preview.Width / 44d));
        int left = preview.Width - 15 * unit, top = preview.Height - 18 * unit;
        using var grip = new SolidBrush(ToastCardStyle.Surface);
        using var dot = new SolidBrush(ToastCardStyle.Accent);
        graphics.FillRectangle(grip, left, top, 14 * unit, 17 * unit);
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 2; column++)
            graphics.FillEllipse(dot, left + (4 + 4 * column) * unit, top + (4 + 4 * row) * unit, 2 * unit, 2 * unit);
        if (previewHover)
        {
            using var outline = new Pen(ToastCardStyle.Accent, unit);
            graphics.DrawRectangle(outline, 0, 0, preview.Width - 1, preview.Height - 1);
        }
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.ExStyle |= ActionExtendedStyle; p.Style &= ~0x10000000; return p; } }
    internal bool Prepare(Rectangle bounds, int dpi, NotificationActions value, out string? warning)
    {
        Hide(); ready = false; actions = value; Bounds = bounds;
        float scale = dpi / 96f;
        int inset = Math.Max(1, (int)Math.Round(12 * scale)), gap = Math.Max(1, (int)Math.Round(8 * scale));
        int height = Math.Min(ClientSize.Height, Math.Max(1, (int)Math.Round(32 * scale)));
        int top = Math.Max(0, ClientSize.Height - height - Math.Min(inset, ClientSize.Height / 4));
        preview.Bounds = ToastCardStyle.PreviewBounds(dpi);
        preview.Image = value.Thumbnail; preview.Visible = value.Thumbnail is not null;
        SetEditorMenu(value.Editors ?? []);
        tips.SetToolTip(preview, value.Edit is null ? "フォルダや対応アプリへドラッグできます。Ctrlを押すとコピー。"
            : "ダブルクリックで編集アプリを開きます。フォルダや対応アプリへドラッグできます。Ctrlを押すとコピー。");
        if (editorMenu.Items.Count != 0)
            tips.SetToolTip(preview, tips.GetToolTip(preview) + " 右クリックで編集アプリを選べます。");
        preview.Cursor = value.Drag is null ? Cursors.Default : Cursors.Hand;
        if (actionFont is null || actionFont.Size != 14 * scale)
        {
            Font? previous = actionFont; actionFont = new Font("Yu Gothic UI", 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = actionFont; previous?.Dispose();
        }
        folder.Visible = value.OpenFolder is not null;
        edit.Visible = value.Edit is not null;
        retry.Visible = value.Retry is not null;
        var buttons = new List<ActionButton>(3);
        if (value.OpenFolder is not null) buttons.Add(folder);
        if (value.Edit is not null) buttons.Add(edit);
        if (value.Retry is not null) buttons.Add(retry);
        var region = new Region(); region.MakeEmpty();
        if (buttons.Count == 3)
        {
            PlaceRow([retry], top);
            buttons.RemoveAt(2);
            top = Math.Max(0, top - height - gap);
        }
        if (buttons.Count != 0) PlaceRow(buttons, top);
        if (value.Thumbnail is not null) region.Union(preview.Bounds);
        Region? oldRegion = Region; Region = region; oldRegion?.Dispose();
        _ = Handle;
        CaptureProtection result = protect(Handle);
        warning = result.Warning;
        if (!result.Success) { protectedHandle = 0; SetLayeredWindowAttributes(Handle, 0, 0, 2); return false; }
        protectedHandle = Handle; ready = true; SetLayeredWindowAttributes(Handle, 0, 255, 2); return true;

        void PlaceRow(IReadOnlyList<ActionButton> row, int y)
        {
            int rowInset = Math.Min(inset, Math.Max(0, (ClientSize.Width - row.Count) / 2));
            int rowGap = Math.Min(gap, Math.Max(0, ClientSize.Width - rowInset * 2 - row.Count));
            int maxWidth = Math.Max(1, (ClientSize.Width - 2 * rowInset - rowGap * (row.Count - 1)) / row.Count);
            int right = ClientSize.Width - rowInset;
            for (int i = row.Count - 1; i >= 0; i--)
            {
                ActionButton button = row[i];
                int width = Math.Min(maxWidth, TextRenderer.MeasureText(button.Text, Font).Width + 2 * inset);
                button.Radius = ToastCardStyle.Scale(6, dpi);
                button.SetBounds(Math.Max(0, right - width), y, width, height);
                using var shape = ToastCardStyle.Rounded(button.Bounds, button.Radius);
                region.Union(shape);
                right -= width + rowGap;
            }
        }
    }
    private void SetEditorMenu(IReadOnlyList<NotificationEditorChoice> choices)
    {
        editorMenu.Close();
        foreach (ToolStripItem item in editorMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        if (choices.Count == 0) return;
        var editWith = new ToolStripMenuItem("アプリを選んで編集") { Name = "NotificationEditWith" };
        if (editWith.DropDown is ToolStripDropDown submenu)
            submenu.Opening += (_, e) => e.Cancel = !protect(submenu.Handle).Success;
        foreach (var choice in choices)
        {
            var item = new ToolStripMenuItem(choice.Name);
            item.Click += (_, _) => InvokeAction(choice.Open);
            editWith.DropDownItems.Add(item);
        }
        editorMenu.Items.Add(editWith);
    }

    internal void ShowProtected() { if (ready && protectedHandle == Handle && nativeShow) CaptureToastForm.ShowNative(this); }
    private void InvokeAction(Action? action)
    {
        if (action is null || !ready) return;
        ready = false; actions = null; editorMenu.Close(); Hide(); action();
    }
    public new void Hide() { base.Hide(); }
    internal void Clear() { ready = false; actions = null; SetEditorMenu([]); CancelGesture(); preview.Image = null; base.Hide(); }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); protectedHandle = 0; ready = false; SetLayeredWindowAttributes(Handle, 0, 0, 2); }
    protected override void OnHandleDestroyed(EventArgs e)
    { protectedHandle = 0; ready = false; base.OnHandleDestroyed(e); }
    protected override void SetVisibleCore(bool value)
    { base.SetVisibleCore(value && ready && protectedHandle == Handle); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = 3; return; }
        if (m.Msg == 0x84) { m.Result = 1; return; }
        if (m.Msg == 0x46 && m.LParam != 0 && protectedHandle != m.HWnd)
        {
            var position = Marshal.PtrToStructure<WindowPosition>(m.LParam);
            position.Flags = (position.Flags & ~0x40u) | 0x80u;
            Marshal.StructureToPtr(position, m.LParam, false);
        }
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { actions = null; CancelGesture(); preview.Image = null; editorMenu.Dispose(); tips.Dispose(); actionFont?.Dispose(); actionFont = null; } base.Dispose(disposing); }

    private sealed class ActionButton : Button
    {
        internal int Radius = 6;
        private bool hovered;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ToastCardStyle.Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = ClientRectangle;
            if (bounds.Width > 2 && bounds.Height > 2) bounds.Inflate(-1, -1);
            using var shape = ToastCardStyle.Rounded(bounds, Radius);
            Color color = hovered ? Color.FromArgb(220, 235, 239) : Color.FromArgb(237, 244, 246);
            if (Capture && MouseButtons == MouseButtons.Left) color = Color.FromArgb(204, 224, 230);
            using var fill = new SolidBrush(color);
            using var outline = new Pen(Color.FromArgb(186, 209, 215));
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(outline, shape);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
}
