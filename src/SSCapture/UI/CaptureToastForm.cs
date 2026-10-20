using System.Runtime.InteropServices;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CaptureToastForm : Form, ICaptureToast
{
    internal const uint ExcludeFromCapture = 0x11;
    internal const int NotificationWidth = 392, NotificationHeight = 68;
    private const int WsVisible = 0x10000000;
    private const int WsExTransparent = 0x00000020;
    internal const int ToastExtendedStyle = 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */
        | 0x00000020 /* TRANSPARENT */ | 0x00080000 /* LAYERED */ | 0x00000008 /* TOPMOST */;
    private readonly Label message;
    private readonly Func<nint, CaptureProtection> protect;
    private readonly Action<CaptureToastForm> show;
    private readonly Func<(Rectangle WorkArea, int Dpi)>? location;
    private Action? activation;
    private nint protectedHandle;
    private string? protectionWarning;
    private Font? notificationFont;
    private int displayDpi = 96;
    public Rectangle DisplayBounds => Bounds;
    public int DisplayDpi => displayDpi;

    internal CaptureToastForm(Func<nint, CaptureProtection>? protect = null,
        Action<CaptureToastForm>? show = null, Func<(Rectangle WorkArea, int Dpi)>? location = null)
    {
        this.protect = protect ?? ProtectNative;
        this.show = show ?? ShowNative;
        this.location = location;
        Name = "CaptureToastForm";
        Text = WindowTitle.Create("完了");
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        // Form.TopMost makes WinForms SetVisibleCore explicitly focus this form,
        // even with ShowWithoutActivation. Keep topmost in native styles instead.
        ControlBox = MinimizeBox = MaximizeBox = false;
        BackColor = Color.FromArgb(28, 43, 50);
        ForeColor = Color.White;
        Font = new Font("Yu Gothic UI", 11F);
        ClientSize = new Size(NotificationWidth, NotificationHeight);
        var layout = new TableLayoutPanel { Name = "CaptureToastLayout", Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 1, Padding = new Padding(16, 10, 16, 10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        message = new Label { Name = "CaptureToastMessage", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Margin = Padding.Empty, UseMnemonic = false };
        layout.Controls.Add(message, 0, 0);
        MouseClick += ActivateOnClick;
        layout.MouseClick += ActivateOnClick;
        message.MouseClick += ActivateOnClick;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= ToastExtendedStyle;
            if (activation is not null) parameters.ExStyle &= ~WsExTransparent; // Only saved results accept clicks.
            parameters.Style &= ~WsVisible;
            return parameters;
        }
    }

    public bool TryShow(string text, bool hasFailure, NotificationCorner corner, out string? warning,
        Action? activate = null, NotificationDisplay display = NotificationDisplay.Captured, Rectangle? captureBounds = null)
    {
        warning = null;
        try
        {
            activation = activate;
            Cursor = activate is null ? Cursors.Default : Cursors.Hand;
            if (IsHandleCreated && !ApplyClickThroughStyle())
            {
                Hide(); warning = "完了表示のクリック状態を設定できないため、表示を中止しました。"; return false;
            }
            var monitor = location?.Invoke() ?? NotificationPlacement.Current(display, captureBounds);
            displayDpi = monitor.Dpi;
            Rectangle bounds = CalculateBounds(monitor.WorkArea, monitor.Dpi, corner, Math.Clamp(text.Count(c => c == '\n') + 1, 1, 4));
            float scale = monitor.Dpi / 96f;
            float fontSize = (44f / 3) * scale;
            if (notificationFont is null || notificationFont.Size != fontSize)
            {
                // Control.Font can keep its existing instance when the new font
                // compares equal. Reuse it at the same DPI instead of disposing
                // the font that the label will still use during its next paint.
                Font? oldFont = notificationFont;
                notificationFont = new Font("Yu Gothic UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                message.Font = notificationFont;
                oldFont?.Dispose();
            }
            Controls[0].Padding = new Padding((int)Math.Round(16 * scale), (int)Math.Round(10 * scale),
                (int)Math.Round(16 * scale), (int)Math.Round(10 * scale));
            // 範囲文言は改行だけでなく折返しも含めて必要な高さを測る。
            int textWidth = Math.Max(1, bounds.Width - Controls[0].Padding.Horizontal);
            Size measured = TextRenderer.MeasureText(text, message.Font, new Size(textWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int margin = monitor.WorkArea.Bottom - bounds.Bottom;
            int height = Math.Min(Math.Max(bounds.Height, measured.Height + Controls[0].Padding.Vertical),
                monitor.WorkArea.Height - margin * 2);
            bounds = new Rectangle(bounds.X, bounds.Bottom - height, bounds.Width, height);
            BackColor = hasFailure ? Color.FromArgb(99, 55, 25) : Color.FromArgb(28, 43, 50);
            message.Text = text;
            Bounds = bounds;
            _ = Handle;
            if (!EnsureProtection()) { Hide(); warning = protectionWarning; return false; }
            show(this);
            if (protectedHandle != Handle) { Hide(); warning = protectionWarning; return false; }
            return true;
        }
        catch (Exception ex)
        {
            Hide(); warning = "完了表示を出せません: " + ex.Message; return false;
        }
    }

    public new void Hide()
    {
        activation = null;
        base.Hide();
        if (IsHandleCreated) ApplyClickThroughStyle();
    }

    private bool ApplyClickThroughStyle()
    {
        const int extendedStyle = -20; // GWL_EXSTYLE
        nint window = Handle;
        nint current = IntPtr.Size == 8 ? GetWindowLongPtr64(window, extendedStyle) : GetWindowLong32(window, extendedStyle);
        // This form always has NOACTIVATE/TOOLWINDOW/LAYERED styles, so zero is
        // an error, rather than an ambiguous successful read or previous value.
        if (current == 0) return false;
        nint desired = activation is null ? current | WsExTransparent : current & ~WsExTransparent;
        if (current == desired) return true;
        nint previous = IntPtr.Size == 8 ? SetWindowLongPtr64(window, extendedStyle, desired)
            : SetWindowLong32(window, extendedStyle, (int)desired);
        if (previous == 0) return false;
        // UpdateStyles also reapplies regular Form styles and frame positioning.
        // Change only click-through, without disturbing visibility or activation.
        return SetWindowPos(window, 0, 0, 0, 0, 0,
            0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x0004 /* SWP_NOZORDER */
            | 0x0010 /* SWP_NOACTIVATE */ | 0x0020 /* SWP_FRAMECHANGED */ | 0x0200 /* SWP_NOOWNERZORDER */);
    }

    private void ActivateOnClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !Visible || activation is not { } action) return;
        Hide();
        action();
    }

    internal static Rectangle CalculateBounds(Rectangle workArea, int dpi, NotificationCorner corner, int lines = 1)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentException("通知先の作業領域がありません。");
        if ((long)workArea.Left + workArea.Width > int.MaxValue || (long)workArea.Top + workArea.Height > int.MaxValue)
            throw new ArgumentException("通知先の作業領域が扱える座標を超えています。");
        if (dpi is < 48 or > 960 || !Enum.IsDefined(corner) || lines is < 1 or > 4) throw new ArgumentException("通知位置の設定が不正です。");
        int margin = Math.Min((int)Math.Round(12 * dpi / 96d), Math.Max(0, (Math.Min(workArea.Width, workArea.Height) - 1) / 2));
        int width = Math.Min((int)Math.Round(NotificationWidth * dpi / 96d), workArea.Width - margin * 2);
        int height = Math.Min((int)Math.Round((NotificationHeight + Math.Max(0, lines - 2) * 22) * dpi / 96d), workArea.Height - margin * 2);
        long left = corner == NotificationCorner.BottomLeft ? (long)workArea.Left + margin : (long)workArea.Left + workArea.Width - margin - width;
        long top = (long)workArea.Top + workArea.Height - margin - height;
        if (left is < int.MinValue or > int.MaxValue || top is < int.MinValue or > int.MaxValue)
            throw new ArgumentException("通知の表示座標が扱える範囲を超えています。");
        return new Rectangle((int)left, (int)top, width, height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // CreateParams forbids an initially visible handle. An unprotected replacement
        // handle remains fully transparent even if a native caller attempts to show it.
        SetLayeredWindowAttributes(Handle, 0, 0, 2);
        EnsureProtection();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    { protectedHandle = 0; base.OnHandleDestroyed(e); }

    protected override void SetVisibleCore(bool value)
    {
        if (value)
        {
            _ = Handle;
            if (!EnsureProtection()) { base.SetVisibleCore(false); return; }
        }
        base.SetVisibleCore(value);
    }

    private bool EnsureProtection()
    {
        CaptureProtection result;
        try { result = protect(Handle); }
        catch (Exception ex) { result = new CaptureProtection(false, "完了表示を画面取得から除外できません: " + ex.Message); }
        if (!result.Success)
        {
            protectedHandle = 0;
            protectionWarning = result.Warning ?? "完了表示を画面取得から除外できないため、表示を中止しました。";
            SetLayeredWindowAttributes(Handle, 0, 0, 2);
            return false;
        }
        protectedHandle = Handle;
        protectionWarning = null;
        SetLayeredWindowAttributes(Handle, 0, 245, 2);
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021 /* WM_MOUSEACTIVATE */) { m.Result = 3 /* MA_NOACTIVATE */; return; }
        if (m.Msg == 0x0084 /* WM_NCHITTEST */) { m.Result = activation is null ? -1 /* HTTRANSPARENT */ : 1 /* HTCLIENT */; return; }
        if (m.Msg == 0x0046 /* WM_WINDOWPOSCHANGING */ && m.LParam != 0 && protectedHandle != m.HWnd)
        {
            WindowPosition position = Marshal.PtrToStructure<WindowPosition>(m.LParam);
            if ((position.Flags & 0x0040 /* SWP_SHOWWINDOW */) != 0)
            {
                position.Flags = (position.Flags & ~0x0040u) | 0x0080u /* SWP_HIDEWINDOW */;
                Marshal.StructureToPtr(position, m.LParam, false);
            }
        }
        base.WndProc(ref m);
    }

    internal static CaptureProtection CheckProtection(nint handle, Func<nint, uint, bool> set,
        Func<nint, (bool Success, uint Affinity)> get)
    {
        if (!set(handle, ExcludeFromCapture)) return new(false, "完了表示を画面取得から除外できないため、表示を中止しました（除外設定失敗）。");
        var actual = get(handle);
        return actual.Success && actual.Affinity == ExcludeFromCapture ? new(true, null)
            : new(false, "完了表示を画面取得から除外できないため、表示を中止しました（除外状態を確認できません）。");
    }

    private static CaptureProtection ProtectNative(nint handle) => CheckProtection(handle, SetWindowDisplayAffinity,
        window => (GetWindowDisplayAffinity(window, out uint affinity), affinity));

    private static void ShowNative(CaptureToastForm form)
    {
        if (!form.Visible) form.Show();
        ShowWindow(form.Handle, 4 /* SW_SHOWNOACTIVATE */);
        SetWindowPos(form.Handle, -1 /* HWND_TOPMOST */, form.Left, form.Top, form.Width, form.Height,
            0x0010 /* SWP_NOACTIVATE */ | 0x0040 /* SWP_SHOWWINDOW */);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { notificationFont?.Dispose(); notificationFont = null; }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint window, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

internal readonly record struct CaptureProtection(bool Success, string? Warning);
