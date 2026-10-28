using System.Drawing.Drawing2D;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class PinnedImageForm : BrandedForm
{
    internal PinnedImageDocument Document { get; }
    internal float Zoom { get; private set; } = 1;
    internal Point OriginalLocation { get; }
    private readonly Func<Bitmap, Task> copyImage;
    private readonly Func<Bitmap, Task<string>> saveImage;
    private readonly Action<string, bool> report;
    private readonly ScreenshotColorDepth colorDepth;
    private Bitmap rendered;
    private readonly List<PointF> points = [];
    private PinnedGesture? gesture;
    private PinnedDrawingTool strokeTool;
    private Point startScreen, startLocation;
    private PointF startImage, lastImage;
    private TextBox? textEditor;
    private Font? textFont;
    private bool exporting;
    private bool closeAfterExport;
    private readonly ToolTip tips = new() { ShowAlways = true, AutoPopDelay = 20000 };

    internal PinnedImageForm(Bitmap image, Point location, AppSettings settings, Action<string, bool>? report = null,
        Func<Bitmap, Task>? copyImage = null, Func<Bitmap, Task<string>>? saveImage = null) : base("ピン留め")
    {
        Name = "PinnedImageForm";
        Document = new(image);
        rendered = Document.Render();
        OriginalLocation = location;
        this.report = report ?? ((_, _) => { });
        this.copyImage = copyImage ?? ClipboardWriter.CopyAsync;
        this.saveImage = saveImage ?? (bitmap => SavePinnedAsync(bitmap, settings));
        colorDepth = settings.ScreenshotColorDepth;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.White;
        Bounds = new(location, image.Size);
        ContextMenuStrip = CreateMenu();
        tips.SetToolTip(this, "ドラッグ: 移動 / Ctrl: 描画 / Ctrl+Shift: 水平・垂直線 / Shift: 範囲 / 右クリック: メニュー");
        Document.Changed += DocumentChanged;
    }

    private void DocumentChanged(object? sender, EventArgs args)
    {
        var next = Document.Render();
        rendered.Dispose(); rendered = next;
        UpdateSize(); Invalidate();
    }
    private void UpdateSize() => ClientSize = new(Math.Max(1, (int)Math.Round(Document.Size.Width * Zoom)),
        Math.Max(1, (int)Math.Round(Document.Size.Height * Zoom)));
    internal void SetZoom(float zoom, Point anchor)
    {
        CommitText();
        if (!float.IsFinite(zoom)) throw new ArgumentOutOfRangeException(nameof(zoom));
        var imagePoint = PinnedImageDocument.ImagePoint(anchor, Zoom);
        var screenPoint = PointToScreen(anchor);
        float maximum = Math.Min(8, 32760f / Math.Max(Document.Size.Width, Document.Size.Height));
        Zoom = Math.Clamp(zoom, 0.1f, Math.Max(0.1f, maximum));
        UpdateSize();
        Location = new(screenPoint.X - (int)Math.Round(imagePoint.X * Zoom), screenPoint.Y - (int)Math.Round(imagePoint.Y * Zoom));
        Invalidate();
    }
    internal void ResetView()
    {
        CommitText(); Zoom = 1; UpdateSize(); Location = OriginalLocation; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = Zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(rendered, ClientRectangle, new Rectangle(Point.Empty, rendered.Size), GraphicsUnit.Pixel);
        var state = e.Graphics.Save();
        e.Graphics.ScaleTransform(Zoom, Zoom);
        if (gesture is PinnedGesture.Draw or PinnedGesture.AxisLine && points.Count > 0)
            PinnedImageDocument.PaintAnnotation(e.Graphics, Document.MakeAnnotation(strokeTool, points));
        if (Document.Selection is Rectangle selection)
        {
            using var shade = new SolidBrush(Color.FromArgb(35, Color.DodgerBlue));
            e.Graphics.FillRectangle(shade, selection);
            using var outline = new Pen(Color.DodgerBlue, 1 / Zoom) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(outline, selection);
        }
        e.Graphics.Restore(state);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || exporting) return;
        CommitText();
        Focus();
        gesture = PinnedImageDocument.Gesture(ModifierKeys);
        startScreen = PointToScreen(e.Location); startLocation = Location;
        startImage = lastImage = Document.Clamp(PinnedImageDocument.ImagePoint(e.Location, Zoom));
        points.Clear(); points.Add(startImage);
        strokeTool = gesture == PinnedGesture.AxisLine ? PinnedDrawingTool.Line : Document.Tool;
        if (gesture == PinnedGesture.Draw && strokeTool == PinnedDrawingTool.Text)
        { gesture = null; BeginText(startImage); return; }
        if (gesture == PinnedGesture.Select) Document.ClearSelection();
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        lastImage = Document.Clamp(PinnedImageDocument.ImagePoint(e.Location, Zoom));
        if (gesture is null) return;
        if (gesture == PinnedGesture.Move)
        {
            Point screen = PointToScreen(e.Location);
            Location = new(startLocation.X + screen.X - startScreen.X, startLocation.Y + screen.Y - startScreen.Y);
            return;
        }
        UpdateGesture(lastImage); Invalidate();
    }
    private void UpdateGesture(PointF end)
    {
        if (gesture == PinnedGesture.Select) { Document.Select(startImage, end); return; }
        if (gesture == PinnedGesture.AxisLine) end = PinnedImageDocument.AxisEnd(startImage, end);
        if (strokeTool == PinnedDrawingTool.Freehand)
        {
            if (points.Count < 100_000 && (points[^1] != end)) points.Add(end);
        }
        else { if (points.Count == 1) points.Add(end); else points[^1] = end; }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || gesture is null) return;
        var finished = gesture;
        if (finished != PinnedGesture.Move) UpdateGesture(Document.Clamp(PinnedImageDocument.ImagePoint(e.Location, Zoom)));
        PointF[] stroke = points.ToArray();
        gesture = null; Capture = false;
        if (finished is PinnedGesture.Draw or PinnedGesture.AxisLine) Document.Add(strokeTool, stroke);
        points.Clear(); Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) { gesture = null; points.Clear(); Invalidate(); }
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (gesture is not null || textEditor is not null) return;
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Document.PenWidth = Math.Clamp(Document.PenWidth + Math.Sign(e.Delta), 1, 128);
            return;
        }
        SetZoom(Zoom * (e.Delta > 0 ? 1.2f : 1 / 1.2f), e.Location);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        bool control = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
        if ((keyData & Keys.Alt) != 0) return base.ProcessCmdKey(ref msg, keyData);
        if (textEditor is not null)
        {
            if (code == Keys.Escape) { FinishText(false); return true; }
            if (code == Keys.Enter && !shift) { CommitText(); return true; }
            if (control && code is Keys.S or Keys.W) CommitText();
            else return base.ProcessCmdKey(ref msg, keyData);
        }
        if (gesture is not null && code != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        if (control)
        {
            switch (code)
            {
                case Keys.C: _ = CopyAsync(); return true;
                case Keys.S: _ = SaveAsync(); return true;
                case Keys.W: Close(); return true;
                case Keys.Z: Document.Undo(); return true;
                case Keys.Y: Document.Redo(); return true;
            }
        }
        else
        {
            switch (code)
            {
                case Keys.Escape: gesture = null; points.Clear(); Capture = false; Document.ClearSelection(); Invalidate(); return true;
                case Keys.L: Document.Rotate(false); return true;
                case Keys.R when !shift: Document.Rotate(true); return true;
                case Keys.T: BeginText(lastImage); return true;
                case Keys.E: Document.Clear(); return true;
            }
            Color? color = code switch { Keys.R when shift => Color.Red, Keys.G => Color.Lime, Keys.B => Color.Blue,
                Keys.Y => Color.Yellow, Keys.O => Color.Orange, Keys.P => Color.Magenta, _ => null };
            if (color is Color selected) { Document.PenColor = selected; Document.Highlighter = shift; return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private void BeginText(PointF location)
    {
        CommitText();
        location = Document.Clamp(location);
        textFont = new Font("Yu Gothic UI", Math.Max(1, Math.Max(12, Document.PenWidth * 6) * Zoom), FontStyle.Regular, GraphicsUnit.Pixel);
        textEditor = new TextBox { Name = "PinnedTextInput", Multiline = true, MaxLength = 4096, AcceptsReturn = true,
            Font = textFont, ForeColor = Document.PenColor, BackColor = Color.White,
            Bounds = new(Math.Clamp((int)(location.X * Zoom), 0, Math.Max(0, ClientSize.Width - 80)),
                Math.Clamp((int)(location.Y * Zoom), 0, Math.Max(0, ClientSize.Height - 32)),
                Math.Max(60, Math.Min(360, ClientSize.Width)), Math.Max(28, Math.Min(100, ClientSize.Height))), Tag = location };
        textEditor.Tag = new PointF(textEditor.Left / Zoom, textEditor.Top / Zoom);
        tips.SetToolTip(textEditor, "Enter: 確定 / Shift+Enter: 改行 / Esc: 取り消し");
        Controls.Add(textEditor); textEditor.Focus();
        textEditor.LostFocus += (_, _) => { if (textEditor is not null && !IsDisposed) CommitText(); };
    }
    private void CommitText() => FinishText(true);
    private void FinishText(bool commit)
    {
        var editor = textEditor;
        if (editor is null) return;
        textEditor = null;
        bool restoreFocus = editor.ContainsFocus;
        string text = editor.Text;
        PointF location = (PointF)editor.Tag!;
        Controls.Remove(editor); editor.Dispose(); textFont?.Dispose(); textFont = null;
        if (commit && !string.IsNullOrWhiteSpace(text)) Document.Add(PinnedDrawingTool.Text, [location], text);
        if (restoreFocus && !Disposing) Focus();
    }
    private Bitmap Export(bool selected)
    {
        var image = Document.Render(selected);
        if (colorDepth == ScreenshotColorDepth.Original) return image;
        try { return ScreenshotColors.Apply(image, colorDepth); }
        finally { image.Dispose(); }
    }
    internal async Task CopyAsync()
    {
        if (exporting) return;
        CommitText(); exporting = true;
        try { using var image = Export(true); await copyImage(image); Notify("ピン留め画像をコピーしました。", false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { Notify("コピーできませんでした: " + ex.Message, true); }
        finally { FinishExport(); }
    }
    internal async Task SaveAsync()
    {
        if (exporting) return;
        CommitText(); exporting = true;
        try { using var image = Document.Render(); string path = await saveImage(image); Notify("保存しました: " + path, false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { Notify("保存できませんでした: " + ex.Message, true); }
        finally { FinishExport(); }
    }
    private void FinishExport()
    {
        exporting = false;
        if (closeAfterExport && !IsDisposed) Close();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (exporting)
        {
            // 最後の窓を閉じるCLIでも、借用画像の配送完了までメッセージループを保つ。
            closeAfterExport = true;
            e.Cancel = true;
        }
    }
    private void Notify(string message, bool error)
    {
        report(message, error);
        if (!IsDisposed && Visible) tips.Show(message, this, Math.Min(12, Width - 1), Math.Min(12, Height - 1), 3500);
    }
    private static async Task<string> SavePinnedAsync(Bitmap image, AppSettings settings)
    {
        string? saved = null;
        using var output = new OutputRouter(Path.Combine(AppContext.BaseDirectory, "data", "work"), delivered: result =>
        { saved = result.SavedPath; result.NotificationImage?.Dispose(); });
        await SSCapture.Capture.InstantCapture.DeliverAsync(image, settings with { DefaultDestination = OutputDestination.File,
            ScreenshotPaddingPixels = 0, MetadataMode = CaptureMetadataMode.None, KeepHistory = false,
            OpenFolderAfterSave = false, NotificationDurationSeconds = 0 }, DateTimeOffset.Now, output,
            regionName: "ピン留め", preserveOutputs: true);
        return saved ?? throw new IOException("保存先を確認できませんでした。");
    }
    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip { Name = "PinnedImageMenu", Font = Font, ShowItemToolTips = true };
        ToolStripMenuItem Item(ToolStripItemCollection target, string text, string name, Action action, string? hint = null)
        { var item = new ToolStripMenuItem(text) { Name = name, ToolTipText = hint ?? "" }; item.Click += (_, _) => { CommitText(); action(); }; target.Add(item); return item; }
        Item(menu.Items, "コピー", "PinnedCopy", () => _ = CopyAsync(), "Ctrl+C。範囲選択中は選択部分をコピーします。");
        Item(menu.Items, "保存", "PinnedSave", () => _ = SaveAsync(), "Ctrl+S。設定済みフォルダ・命名パターンで全体を保存します。");
        var pen = new ToolStripMenuItem("ペン") { Name = "PinnedPen" }; menu.Items.Add(pen);
        foreach (var tool in Enum.GetValues<PinnedDrawingTool>())
        {
            string text = tool switch { PinnedDrawingTool.Freehand => "自由線", PinnedDrawingTool.Line => "直線", PinnedDrawingTool.Rectangle => "矩形",
                PinnedDrawingTool.Ellipse => "楕円", PinnedDrawingTool.Arrow => "矢印", _ => "文字" };
            var item = Item(pen.DropDownItems, text, "PinnedTool" + tool, () => Document.Tool = tool, "Ctrl+ドラッグで描画。Ctrl+Shiftは水平・垂直線。");
            item.Tag = tool;
        }
        var colors = new ToolStripMenuItem("色") { Name = "PinnedColors" }; pen.DropDownItems.Add(colors);
        foreach (var (name, color) in new[] { ("赤", Color.Red), ("緑", Color.Lime), ("青", Color.Blue), ("黄", Color.Yellow), ("橙", Color.Orange), ("ピンク", Color.Magenta) })
            Item(colors.DropDownItems, name, "PinnedColor" + color.Name, () => Document.PenColor = color);
        var highlighter = Item(pen.DropDownItems, "蛍光ペン", "PinnedHighlighter", () => Document.Highlighter = !Document.Highlighter);
        var width = new ToolStripMenuItem("太さ") { Name = "PinnedPenWidth" }; pen.DropDownItems.Add(width);
        foreach (int value in new[] { 1, 3, 5, 10, 20 }) Item(width.DropDownItems, value + "px", "PinnedWidth" + value, () => Document.PenWidth = value, "Ctrl+ホイールでも調整できます。");
        menu.Items.Add(new ToolStripSeparator());
        var undo = Item(menu.Items, "元に戻す", "PinnedUndo", Document.Undo, "Ctrl+Z");
        var redo = Item(menu.Items, "やり直す", "PinnedRedo", Document.Redo, "Ctrl+Y");
        Item(menu.Items, "描画を全消去", "PinnedClear", Document.Clear, "E。Ctrl+Zで取り消せます。");
        Item(menu.Items, "左へ回転", "PinnedRotateLeft", () => Document.Rotate(false), "L / 左90度");
        Item(menu.Items, "右へ回転", "PinnedRotateRight", () => Document.Rotate(true), "R / 右90度");
        Item(menu.Items, "倍率100％・撮影位置へ戻す", "PinnedResetView", ResetView);
        menu.Items.Add(new ToolStripSeparator());
        Item(menu.Items, "閉じる", "PinnedClose", Close, "Ctrl+W");
        menu.Opening += (_, _) =>
        {
            undo.Enabled = Document.CanUndo; redo.Enabled = Document.CanRedo; highlighter.Checked = Document.Highlighter;
            foreach (var item in pen.DropDownItems.OfType<ToolStripMenuItem>())
                if (item.Tag is PinnedDrawingTool tool) item.Checked = tool == Document.Tool;
        };
        return menu;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            gesture = null;
            Document.Changed -= DocumentChanged;
            textEditor = null; textFont?.Dispose(); textFont = null;
            rendered.Dispose(); Document.Dispose(); tips.Dispose(); ContextMenuStrip?.Dispose();
        }
        base.Dispose(disposing);
    }
}
