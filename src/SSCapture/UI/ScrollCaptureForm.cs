using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class ScrollCaptureForm : BrandedForm
{
    private readonly TaskCompletionSource<Bitmap?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 180 };
    private readonly Label status = new() { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Rectangle viewport;
    private readonly ScrollFrameStitcher stitcher;
    private readonly Func<Bitmap> takeFrame;
    private readonly IReadOnlyList<Rectangle> monitors;
    private CaptureWindowsKey? escape;
    private bool pending, stopped, confirming;

    internal ScrollCaptureForm(Rectangle viewport, ScrollFrameStitcher stitcher, Func<Bitmap> takeFrame,
        IReadOnlyList<Rectangle> monitors) : base("スクロール撮影")
    {
        this.viewport = viewport;
        this.stitcher = stitcher;
        this.takeFrame = takeFrame;
        this.monitors = monitors;

        Name = "ScrollCaptureForm";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 112);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), RowCount = 2, ColumnCount = 1 };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        status.Name = "ScrollCaptureStatus";
        body.Controls.Add(status, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = SettingsForm.MakeButton("取り消す", "CancelScrollCapture");
        var confirm = SettingsForm.MakeButton("撮影を確定", "ConfirmScrollCapture", ButtonRole.Primary);
        cancel.Click += (_, _) => Close();
        confirm.Click += (_, _) => Confirm();
        buttons.Controls.AddRange([confirm, cancel]);
        body.Controls.Add(buttons, 0, 1);
        Controls.Add(body);
        AcceptButton = confirm;
        CancelButton = cancel;
        UpdateHeight();
        timer.Tick += (_, _) => _ = SampleAsync();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Location = FindPanelLocation(ViewportWorkAreas(), viewport, Size);
        timer.Start();
        bool protectedWindow = CaptureNative.SetWindowDisplayAffinity(Handle, CaptureNative.ExcludeFromCapture);
        if (!protectedWindow && Bounds.IntersectsWith(viewport))
        {
            StopCollecting("操作窓が対象と重なっています。");
        }
    }

    private static IEnumerable<Rectangle> ViewportWorkAreas() =>
        Screen.AllScreens.Select(screen => screen.WorkingArea);

    internal static Point FindPanelLocation(IEnumerable<Rectangle> workAreas, Rectangle viewport, Size panelSize)
    {
        Rectangle[] ordered = workAreas.OrderByDescending(area => area.IntersectsWith(viewport)).ToArray();
        foreach (Rectangle area in ordered)
        {
            var locations = new[]
            {
                new Point(area.Right - panelSize.Width, area.Bottom - panelSize.Height),
                new Point(area.Left, area.Bottom - panelSize.Height),
                new Point(area.Left, area.Top),
                new Point(area.Right - panelSize.Width, area.Top),
                new Point(area.Left, viewport.Bottom),
                new Point(area.Left, viewport.Top - panelSize.Height),
                new Point(viewport.Right, area.Top),
                new Point(viewport.Left - panelSize.Width, area.Top)
            };
            foreach (Point location in locations)
            {
                var proposed = new Rectangle(location, panelSize);
                if (area.Contains(proposed) && !proposed.IntersectsWith(viewport)) return location;
            }
        }
        return ordered.FirstOrDefault().Location;
    }

    internal Task<Bitmap?> Completion => completion.Task;

    internal void Start(bool interceptEscape)
    {
        if (interceptEscape)
            escape = new CaptureWindowsKey(() => true, () => CaptureNative.OwnsForegroundWindow()
                || CaptureNative.TryCursorPosition(out Point point) && viewport.Contains(point), interceptWindowsKey: false);
        Location = FindPanelLocation(ViewportWorkAreas(), viewport, Size);
        Show();
    }

    private async Task SampleAsync()
    {
        if (escape?.TakeEscape() == true) { Close(); return; }
        if (pending || stopped || !timer.Enabled) return;
        pending = true;
        try
        {
            ScrollFrameResult result = await Task.Run(() =>
            {
                if (!CaptureNative.MonitorBounds().SequenceEqual(monitors))
                    throw new InvalidOperationException("ディスプレイの配置が変わりました。");
                using Bitmap frame = takeFrame();
                return stitcher.Add(frame);
            });
            if (IsDisposed) return;
            if (result == ScrollFrameResult.Limit)
                StopCollecting("画像上限に達しました。");
            else if (result == ScrollFrameResult.Unmatched)
                UpdateHeight("重なりを判定できません。少し戻し、ゆっくり下へスクロールしてください。");
            else
                UpdateHeight();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) StopCollecting(ex.Message);
        }
        finally { pending = false; }
    }


    private void StopCollecting(string reason)
    {
        stopped = true;
        UpdateHeight(reason);
        // Keep the timer running for Escape after reaching the image limit.
    }

    private void UpdateHeight(string? message = null)
    {
        string size = $"{viewport.Width}×{stitcher.Height} px";
        string text = message is null ? size : $"{size}{Environment.NewLine}{message}";
        if (status.Text == text) return;
        status.Text = text;
        status.AccessibleDescription = text;
    }

    private async void Confirm()
    {
        if (confirming) return;
        confirming = true;
        timer.Stop();
        // A sampling task owns the assembler until it returns to the UI thread.
        while (pending && !IsDisposed) await Task.Delay(20);
        if (IsDisposed) return;
        try
        {
            completion.TrySetResult(stitcher.Build());
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            escape?.Dispose();
            completion.TrySetResult(null);
        }
        base.Dispose(disposing);
    }
}
