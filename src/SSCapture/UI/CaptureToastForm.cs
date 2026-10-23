using System.Runtime.InteropServices;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CaptureToastForm : Form, ICaptureToast
{
    internal const uint ExcludeFromCapture = 0x11;
    internal const int NotificationWidth = 392, NotificationHeight = 68;
    private const int WsVisible = 0x10000000;
    internal const int ToastExtendedStyle = 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */
        | 0x00000020 /* TRANSPARENT */ | 0x00080000 /* LAYERED */ | 0x00000008 /* TOPMOST */;
    private readonly Label message;
    private readonly Func<nint, CaptureProtection> protect;
    private readonly Action<CaptureToastForm> show;
    private readonly Func<(Rectangle WorkArea, int Dpi)>? location;
    private NotificationActions? actions;
    private ToastActionForm? actionHost;
    private readonly bool nativeShow;
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
        nativeShow = show is null;
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
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= ToastExtendedStyle;
            parameters.Style &= ~WsVisible;
            return parameters;
        }
    }

    public void SetActions(NotificationActions value) => actions = value;

    public bool TryShow(string text, bool hasFailure, NotificationCorner corner, out string? warning,
        Action? activate = null, NotificationDisplay display = NotificationDisplay.Captured, Rectangle? captureBounds = null)
    {
        warning = null;
        try
        {
            // 本文は常に別プロセスまで透過。旧activate引数で透過を解除しない。
            actionHost?.Hide();
            if (actions is not null) text += Environment.NewLine + actions.RepeatHint;
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
            int margin = Math.Min((int)Math.Round(12 * scale), Math.Max(0, (Math.Min(monitor.WorkArea.Width, monitor.WorkArea.Height) - 1) / 2));
            int actionHeight = actions is null ? 0 : (int)Math.Round(44 * scale);
            int height = Math.Min(Math.Max(bounds.Height, measured.Height + Controls[0].Padding.Vertical),
                monitor.WorkArea.Height - margin * 2 - Math.Min(actionHeight, monitor.WorkArea.Height / 3));
            bounds = PlaceSize(monitor.WorkArea, monitor.Dpi, corner, new(bounds.Width, Math.Max(1, height + actionHeight)));
            Controls[0].Padding = new Padding(Controls[0].Padding.Left, Controls[0].Padding.Top, Controls[0].Padding.Right, Controls[0].Padding.Bottom + actionHeight);
            BackColor = hasFailure ? Color.FromArgb(99, 55, 25) : Color.FromArgb(28, 43, 50);
            message.Text = text;
            Bounds = bounds;
            _ = Handle;
            if (!EnsureProtection()) { Hide(); warning = protectionWarning; return false; }
            if (actions is not null)
            {
                actionHost ??= new ToastActionForm(protect, nativeShow);
                if (!actionHost.Prepare(bounds, monitor.Dpi, actions, out warning)) { Hide(); return false; }
            }
            show(this);
            if (protectedHandle != Handle) { Hide(); warning = protectionWarning; return false; }
            if (actions is not null) actionHost?.ShowProtected();
            return true;
        }
        catch (Exception ex)
        {
            Hide(); warning = "完了表示を出せません: " + ex.Message; return false;
        }
    }

    public new void Hide()
    {
        actionHost?.Clear();
        actions = null;
        base.Hide();
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
        return PlaceSize(workArea, dpi, corner, new(width, height));
    }

    internal static Rectangle PlaceSize(Rectangle workArea, int dpi, NotificationCorner corner, Size size)
    {
        int margin = Math.Min((int)Math.Round(12 * dpi / 96d), Math.Max(0, (Math.Min(workArea.Width, workArea.Height) - 1) / 2));
        int width = Math.Clamp(size.Width, 1, Math.Max(1, workArea.Width - 2 * margin));
        int height = Math.Clamp(size.Height, 1, Math.Max(1, workArea.Height - 2 * margin));
        int column = corner switch { NotificationCorner.TopLeft or NotificationCorner.MiddleLeft or NotificationCorner.BottomLeft => 0,
            NotificationCorner.TopCenter or NotificationCorner.Center or NotificationCorner.BottomCenter => 1, _ => 2 };
        int row = corner switch { NotificationCorner.TopLeft or NotificationCorner.TopCenter or NotificationCorner.TopRight => 0,
            NotificationCorner.MiddleLeft or NotificationCorner.Center or NotificationCorner.MiddleRight => 1, _ => 2 };
        long left = (long)workArea.X + (column == 0 ? margin : column == 1 ? (workArea.Width - width) / 2 : workArea.Width - margin - width);
        long top = (long)workArea.Y + (row == 0 ? margin : row == 1 ? (workArea.Height - height) / 2 : workArea.Height - margin - height);
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
    { protectedHandle = 0; actionHost?.Hide(); base.OnHandleDestroyed(e); }

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
        if (m.Msg == 0x0084 /* WM_NCHITTEST */) { m.Result = -1 /* HTTRANSPARENT */; return; }
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

    internal static void ShowNative(Form form)
    {
        if (!form.Visible) form.Show();
        ShowWindow(form.Handle, 4 /* SW_SHOWNOACTIVATE */);
        SetWindowPos(form.Handle, -1 /* HWND_TOPMOST */, form.Left, form.Top, form.Width, form.Height,
            0x0010 /* SWP_NOACTIVATE */ | 0x0040 /* SWP_SHOWWINDOW */);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { actionHost?.Dispose(); notificationFont?.Dispose(); notificationFont = null; }
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
