using System.Runtime.InteropServices;
using SSCapture.Services;

namespace SSCapture.UI;

// 本文のLAYERED/TRANSPARENT窓とは分離。Regionの2矩形だけが入力を受ける。
internal sealed class ToastActionForm : Form
{
    internal const int ActionExtendedStyle = 0x08000000 | 0x80 | 0x80000 | 0x8;
    private readonly Func<nint, CaptureProtection> protect;
    private readonly bool nativeShow;
    private readonly Button folder, edit;
    private nint protectedHandle;
    private NotificationActions? actions;
    private bool ready;
    private Font? actionFont;
    internal ToastActionForm(Func<nint, CaptureProtection> protect, bool nativeShow)
    {
        this.protect = protect; this.nativeShow = nativeShow;
        Name = "CaptureToastActions"; Text = WindowTitle.Create("完了操作");
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None; ControlBox = false;
        BackColor = Color.FromArgb(28, 43, 50);
        folder = Make("フォルダを開く", "NotificationOpenFolder");
        edit = Make("外部アプリで編集", "NotificationEdit");
        Controls.AddRange([folder, edit]);
        folder.Click += (_, _) => InvokeAction(actions?.OpenFolder);
        edit.Click += (_, _) => InvokeAction(actions?.Edit);
    }
    private static Button Make(string text, string name) => new() { Text = text, Name = name,
        FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 106, 119), ForeColor = Color.White, TabStop = false, UseMnemonic = false };
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.ExStyle |= ActionExtendedStyle; p.Style &= ~0x10000000; return p; } }
    internal bool Prepare(Rectangle bounds, int dpi, NotificationActions value, out string? warning)
    {
        Hide(); ready = false; actions = value; Bounds = bounds;
        float scale = dpi / 96f;
        int inset = Math.Max(1, (int)Math.Round(12 * scale)), gap = Math.Max(1, (int)Math.Round(8 * scale));
        int width = Math.Max(1, (ClientSize.Width - 2 * inset - gap) / 2);
        int height = Math.Min(ClientSize.Height, Math.Max(1, (int)Math.Round(32 * scale)));
        int top = Math.Max(0, ClientSize.Height - height - Math.Min(inset, ClientSize.Height / 4));
        folder.SetBounds(inset, top, width, height); edit.SetBounds(inset + width + gap, top, width, height);
        if (actionFont is null || actionFont.Size != 14 * scale)
        {
            Font? previous = actionFont; actionFont = new Font("Yu Gothic UI", 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = actionFont; previous?.Dispose();
        }
        folder.Enabled = value.OpenFolder is not null; edit.Enabled = value.Edit is not null;
        var region = new Region(folder.Bounds); region.Union(edit.Bounds);
        Region? oldRegion = Region; Region = region; oldRegion?.Dispose();
        _ = Handle;
        CaptureProtection result = protect(Handle);
        warning = result.Warning;
        if (!result.Success) { protectedHandle = 0; SetLayeredWindowAttributes(Handle, 0, 0, 2); return false; }
        protectedHandle = Handle; ready = true; SetLayeredWindowAttributes(Handle, 0, 245, 2); return true;
    }
    internal void ShowProtected() { if (ready && protectedHandle == Handle && nativeShow) CaptureToastForm.ShowNative(this); }
    private void InvokeAction(Action? action)
    {
        if (action is null || !ready) return;
        ready = false; actions = null; Hide(); action();
    }
    public new void Hide() { base.Hide(); }
    internal void Clear() { ready = false; actions = null; base.Hide(); }
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
    { if (disposing) { actions = null; actionFont?.Dispose(); actionFont = null; } base.Dispose(disposing); }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
}
