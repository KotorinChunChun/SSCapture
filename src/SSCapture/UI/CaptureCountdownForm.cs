using System.ComponentModel;
using System.Runtime.InteropServices;
using SSCapture.Capture;

namespace SSCapture.UI;

internal sealed class CaptureCountdownForm : Form
{
    private readonly int inset, barHeight;
    private readonly Font countdownFont;
    private CaptureCountdownFrame frame;

    internal CaptureCountdownForm(Rectangle workArea, int dpi)
    {
        Name = "CaptureCountdown";
        Text = WindowTitle.Create("撮影待機");
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = ToastCardStyle.Surface;
        ForeColor = ToastCardStyle.Ink;
        float scale = dpi / 96f;
        inset = Math.Max(1, (int)Math.Round(9 * scale));
        barHeight = Math.Max(1, (int)Math.Round(4 * scale));
        countdownFont = new Font("Yu Gothic UI", 16 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        int width = (int)Math.Round(96 * scale), height = (int)Math.Round(42 * scale);
        int margin = (int)Math.Round(12 * scale);
        Bounds = new(Math.Max(workArea.Left, workArea.Right - width - margin),
            Math.Max(workArea.Top, workArea.Bottom - height - margin), width, height);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= CaptureToastForm.ToastExtendedStyle;
            return parameters;
        }
    }

    internal void ShowCountdown()
    {
        // LAYERED と TRANSPARENT の組み合わせで、他プロセスのウィンドウにも入力を通す。
        if (!SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _ = CaptureNative.SetWindowDisplayAffinity(Handle, CaptureNative.ExcludeFromCapture);
        CaptureToastForm.ShowNative(this);
    }

    internal void UpdateFrame(CaptureCountdownFrame value)
    {
        frame = value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var labelBounds = new Rectangle(inset, 0, ClientSize.Width - 2 * inset, ClientSize.Height - inset - barHeight);
        TextRenderer.DrawText(e.Graphics, $"{frame.Seconds}秒", countdownFont, labelBounds, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var track = new Rectangle(inset, ClientSize.Height - inset - barHeight, labelBounds.Width, barHeight);
        using var trackBrush = new SolidBrush(ToastCardStyle.Muted);
        using var fillBrush = new SolidBrush(ToastCardStyle.Accent);
        e.Graphics.FillRectangle(trackBrush, track);
        track.Width = (int)Math.Round(track.Width * frame.Fraction);
        e.Graphics.FillRectangle(fillBrush, track);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) countdownFont.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
