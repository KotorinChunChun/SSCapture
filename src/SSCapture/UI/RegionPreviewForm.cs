using System.Runtime.InteropServices;

namespace SSCapture.UI;

internal sealed class RegionPreviewForm : Form
{
    private const int EscapeHotkeyId = 0x5353;
    private nint escapeWindow;
    private bool previewActive;
    public RegionPreviewForm(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("範囲の幅と高さは1px以上です。");
        Name = "RegionPreviewForm";
        Text = "SSCapture — 範囲確認（Escで閉じる）";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(0, 180, 190);
        Bounds = bounds;
        var border = new Region(ClientRectangle);
        Rectangle inside = Rectangle.Inflate(ClientRectangle, -4, -4);
        if (inside.Width > 0 && inside.Height > 0) border.Exclude(inside);
        Region = border;
        MouseDown += (_, _) => Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        previewActive = true;
        RegisterEscape();
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (previewActive) RegisterEscape();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    { ReleaseEscape(); base.OnHandleDestroyed(e); }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) ReleaseEscape();
        else if (previewActive) RegisterEscape();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    { previewActive = false; ReleaseEscape(); base.OnFormClosed(e); }

    private void RegisterEscape()
    {
        ReleaseEscape();
        // A border-only window can lose focus to the window under its transparent
        // interior. Reserve just Escape while this explicit preview is visible.
        if (RegisterHotKey(Handle, EscapeHotkeyId, 0x4000 /* MOD_NOREPEAT */, (uint)Keys.Escape)) escapeWindow = Handle;
    }

    private void ReleaseEscape()
    {
        if (escapeWindow == 0) return;
        UnregisterHotKey(escapeWindow, EscapeHotkeyId);
        escapeWindow = 0;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 /* WM_HOTKEY */ && message.WParam == EscapeHotkeyId)
        { Close(); return; }
        base.WndProc(ref message);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void Dispose(bool disposing)
    { if (disposing) { previewActive = false; ReleaseEscape(); } base.Dispose(disposing); }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
