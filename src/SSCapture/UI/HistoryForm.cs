using System.Drawing.Drawing2D;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class HistoryForm : Form
{
    private readonly CaptureHistory history;
    private readonly ListBox list;
    private readonly PictureBox picture;
    private readonly Label details, status;
    private readonly Button copyButton, editButton, removeButton, clearButton;
    private readonly ContextMenuStrip editMenu = new() { Name = "HistoryEditMenu" };
    private readonly ContextMenuStrip ocrMenu = new() { Name = "HistoryOcrMenu" };
    private readonly CancellationTokenSource formLifetime = new();
    private readonly Button? ocrButton;
    private readonly ToolTip tips = new();
    private readonly Func<Guid, Control, CancellationToken, Task<string>>? drag;
    private bool operating, refreshing, refreshPending, disposedResources;
    private Point? dragOrigin;
    private CancellationTokenSource? dragGesture;

    public HistoryForm(CaptureHistory history, Func<Guid, Task<string>> copy,
        Func<IReadOnlyList<ExternalApplication>> applications, Func<Guid, string, Task<string>> editWithApplication,
        Func<Guid, Control, CancellationToken, Task<string>>? drag = null,
        Func<Guid, bool, CancellationToken, Task<string>>? copyOcr = null)
    {
        this.history = history;
        this.drag = drag;
        Name = "HistoryForm";
        Text = WindowTitle.Create("撮影履歴");
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 12F);
        BackColor = Color.FromArgb(247, 249, 251);
        ForeColor = Color.FromArgb(28, 37, 47);
        ClientSize = new Size(1000, 626);
        MinimumSize = new Size(920, 570);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();
        var root = new TableLayoutPanel { Name = "HistoryLayout", Dock = DockStyle.Fill, Padding = new Padding(12),
            ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);
        var body = new TableLayoutPanel { Name = "HistoryBody", Dock = DockStyle.Fill, ColumnCount = 2,
            RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 332));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 0);
        list = new ListBox { Name = "HistoryEntries", Dock = DockStyle.Fill, IntegralHeight = false,
            SelectionMode = SelectionMode.MultiExtended,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 80, Margin = new Padding(0, 0, 10, 0) };
        body.Controls.Add(list, 0, 0);
        var preview = new TableLayoutPanel { Name = "HistoryPreviewPanel", Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        body.Controls.Add(preview, 1, 0);
        picture = new PictureBox { Name = "HistoryPreview", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(224, 229, 234), BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
        if (drag is not null)
        {
            picture.MouseDown += BeginDrag;
            picture.MouseMove += DragPreviewAsync;
            picture.MouseUp += (_, _) => CancelDrag();
            picture.MouseCaptureChanged += (_, _) => { if (!picture.Capture) CancelDrag(); };
        }
        preview.Controls.Add(picture, 0, 0);
        details = new Label { Name = "HistoryDetails", Dock = DockStyle.Fill, AutoEllipsis = true,
            Padding = new Padding(0, 5, 0, 0) };
        preview.Controls.Add(details, 0, 1);
        status = new Label { Name = "HistoryStatus", Dock = DockStyle.Fill, AutoEllipsis = true,
            Padding = new Padding(0, 6, 0, 0) };
        root.Controls.Add(status, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = Padding.Empty };
        var close = Button("閉じる", "CloseHistory", 100, ButtonRole.Neutral);
        close.Click += (_, _) => Close();
        clearButton = Button("履歴を消去", "ClearHistory", 138, ButtonRole.Danger);
        clearButton.Click += (_, _) => ClearHistory();
        tips.SetToolTip(clearButton, "一覧と履歴用の画像を消去します。撮影時に保存したファイルは残ります。");
        removeButton = Button("選択項目を削除", "RemoveHistorySelection", 144, ButtonRole.Danger);
        removeButton.Click += (_, _) => RemoveSelected();
        editButton = Button("画像編集アプリを選ぶ ▾", "EditHistoryImage", 212, ButtonRole.Secondary);
        editButton.ContextMenuStrip = editMenu;
        editButton.Click += (_, _) => editMenu.Show(editButton, new Point(0, editButton.Height));
        editMenu.Opening += (_, e) =>
        {
            while (editMenu.Items.Count > 0) editMenu.Items[0].Dispose();
            if (operating || SelectedImage is not Item target || picture.Image is null) { e.Cancel = true; return; }
            var apps = applications();
            if (apps.Count == 0)
            {
                SetStatus("設定の「外部アプリ」で画像編集アプリを登録してください。");
                e.Cancel = true;
                return;
            }
            foreach (var app in apps)
            {
                string appId = app.Id;
                var choice = new ToolStripMenuItem { Text = app.Name.Replace("&", "&&"), Name = appId };
                choice.Click += async (_, _) =>
                {
                    if (SelectedImage?.Entry.Id == target.Entry.Id)
                        await SendAsync(id => editWithApplication(id, appId));
                };
                editMenu.Items.Add(choice);
            }
            e.Cancel = false;
        };
        copyButton = Button("再コピー", "CopyHistoryImage", 124, ButtonRole.Primary);
        copyButton.Click += async (_, _) => await SendAsync(copy);
        buttons.Controls.AddRange([close, clearButton, removeButton, editButton]);
        if (copyOcr is not null)
        {
            ocrButton = Button("OCRコピー ▾", "CopyHistoryOcr", 120, ButtonRole.Secondary);
            AddOcrItem("文字をコピー", "HistoryCopyText", false);
            AddOcrItem("表をコピー", "HistoryCopyTable", true);
            ocrMenu.Opening += (_, e) => e.Cancel = operating || SelectedImage is null || picture.Image is null;
            ocrButton.Click += (_, _) => ocrMenu.Show(ocrButton, new Point(0, ocrButton.Height));
            picture.ContextMenuStrip = ocrMenu;
            buttons.Controls.Add(ocrButton);

            void AddOcrItem(string label, string name, bool asTable)
            {
                var item = new ToolStripMenuItem(label) { Name = name };
                item.Click += async (_, _) => await SendAsync(id => copyOcr(id, asTable, formLifetime.Token));
                ocrMenu.Items.Add(item);
            }
        }
        buttons.Controls.Add(copyButton);
        root.Controls.Add(buttons, 0, 2);
        CancelButton = close;
        list.DrawItem += DrawEntry;
        list.SelectedIndexChanged += (_, _) => { if (!refreshing && !disposedResources) LoadPreview(); };
        HandleCreated += (_, _) => UpdateRowHeight();
        DpiChanged += (_, _) => UpdateRowHeight();
        RefreshEntries();
    }

    internal void RefreshEntries()
    {
        if (IsDisposed || disposedResources) return;
        if (operating) { refreshPending = true; return; }
        var selected = list.SelectedItems.Cast<Item>().Select(item => item.Entry.Id).ToHashSet();
        refreshing = true;
        list.BeginUpdate();
        try
        {
            ReleaseItems();
            foreach (HistoryEntry entry in history.Entries)
            {
                using Bitmap? image = history.CloneImage(entry.Id);
                if (image is null) continue;
                list.Items.Add(new Item(entry, Thumbnail(image)));
            }
            for (int index = 0; index < list.Items.Count; index++)
                if (selected.Contains(((Item)list.Items[index]).Entry.Id)) list.SetSelected(index, true);
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.SetSelected(0, true);
        }
        finally { list.EndUpdate(); refreshing = false; }
        SetStatus(list.Items.Count == 0 ? "撮影履歴はありません。" : $"撮影履歴 {list.Items.Count}枚");
        LoadPreview();
    }

    private Item? SelectedImage => list.SelectedItems.Count == 1 ? list.SelectedItem as Item : null;

    private void LoadPreview()
    {
        CancelDrag();
        Image? previous = picture.Image;
        picture.Image = null; previous?.Dispose();
        if (SelectedImage is not Item selected) { details.Text = ""; UpdateButtons(); return; }
        Bitmap? image = history.CloneImage(selected.Entry.Id);
        picture.Image = image;
        if (image is null)
        {
            details.Text = "この画像は履歴から解放されています。";
            SetStatus("履歴を開き直すと最新の一覧に更新します。");
        }
        else
        {
            HistoryEntry entry = selected.Entry;
            var (stored, expiresAt) = history.RetentionFor(entry);
            string expiration = expiresAt is { } until ? $"　保持期限 {until.ToLocalTime():yyyy/MM/dd HH:mm:ss}" : "　保持期限なし";
            details.Text = $"{entry.CapturedAt:yyyy/MM/dd HH:mm:ss}　{entry.Size.Width} × {entry.Size.Height} px\n"
                + (stored ? "履歴をディスクに保存済み" : "履歴はメモリー内のみ") + expiration
                + (entry.SavedPath is null ? "" : "\n撮影ファイル " + entry.SavedPath);
        }
        UpdateButtons();
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || operating || SelectedImage is null || picture.Image is null) return;
        CancelDrag();
        dragGesture = new();
        dragOrigin = e.Location;
    }

    private async void DragPreviewAsync(object? sender, MouseEventArgs e)
    {
        if (drag is null || dragOrigin is not Point origin || dragGesture is null || e.Button != MouseButtons.Left
            || !NotificationFileDrag.DragThresholdReached(origin, e.Location, SystemInformation.DragSize)) return;
        dragOrigin = null;
        CancellationToken gesture = dragGesture.Token;
        try { await SendAsync(id => drag(id, picture, gesture)); }
        finally { CancelDrag(); }
    }

    private void CancelDrag()
    {
        dragOrigin = null;
        dragGesture?.Cancel();
        dragGesture?.Dispose();
        dragGesture = null;
    }

    internal async Task SendAsync(Func<Guid, Task<string>> action)
    {
        if (operating || SelectedImage is not Item selected) return;
        if (history.Find(selected.Entry.Id) is null)
        { RefreshEntries(); SetStatus("この画像は履歴から解放されています。", true); return; }
        operating = true; list.Enabled = false; UpdateButtons(); SetStatus("処理中…");
        string? result = null;
        bool error = false;
        try { result = await action(selected.Entry.Id); }
        catch (Exception ex) { result = ex.Message; error = true; }
        finally
        {
            operating = false;
            if (!IsDisposed && !disposedResources)
            {
                list.Enabled = true;
                if (refreshPending) { refreshPending = false; RefreshEntries(); }
                UpdateButtons();
                SetStatus(result ?? "処理が完了しました。", error);
            }
        }
    }

    private void ClearHistory()
    {
        if (operating) return;
        history.Clear(); RefreshEntries();
        SetStatus("メモリー内の履歴を消去しました。保存済みファイルは残っています。");
    }

    private void RemoveSelected()
    {
        if (operating || list.SelectedItems.Count == 0) return;
        int removed = history.Remove(list.SelectedItems.Cast<Item>().Select(item => item.Entry.Id));
        RefreshEntries();
        SetStatus($"選択した履歴を{removed}枚削除しました。保存済みファイルは残っています。");
    }

    private void UpdateButtons()
    {
        bool selected = SelectedImage is not null && picture.Image is not null;
        copyButton.Enabled = editButton.Enabled = !operating && selected;
        if (ocrButton is not null) ocrButton.Enabled = !operating && selected;
        removeButton.Enabled = !operating && list.SelectedItems.Count > 0;
        clearButton.Enabled = !operating && list.Items.Count > 0;
        bool canDrag = !operating && selected && drag is not null;
        picture.Cursor = canDrag ? Cursors.Hand : Cursors.Default;
        tips.SetToolTip(picture, canDrag ? "フォルダへドラッグして移動。Ctrlを押すとコピー。対応アプリへドロップして開きます。" : null);
    }

    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= list.Items.Count) return;
        var item = (Item)list.Items[e.Index];
        int padding = Math.Max(4, 4 * DeviceDpi / 96), imageWidth = 96 * DeviceDpi / 96;
        var thumb = new Rectangle(e.Bounds.X + padding, e.Bounds.Y + padding, imageWidth, e.Bounds.Height - padding * 2);
        e.Graphics.DrawImage(item.Thumbnail, thumb);
        var text = new Rectangle(thumb.Right + padding, e.Bounds.Y + padding,
            Math.Max(1, e.Bounds.Right - thumb.Right - padding * 2), e.Bounds.Height - padding * 2);
        Color color = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : ForeColor;
        string value = $"{item.Entry.CapturedAt:MM/dd HH:mm:ss}\n{item.Entry.Size.Width} × {item.Entry.Size.Height} px\n"
            + (item.Entry.SavedPath is null ? "メモリー内のみ" : "ファイル保存済み");
        TextRenderer.DrawText(e.Graphics, value, Font, text, color,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        e.DrawFocusRectangle();
    }

    private void UpdateRowHeight() => list.ItemHeight = Math.Max(80 * DeviceDpi / 96, Font.Height * 3 + 8 * DeviceDpi / 96);

    private static Bitmap Thumbnail(Bitmap image)
    {
        var thumbnail = new Bitmap(128, 96);
        using Graphics graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float ratio = Math.Min(thumbnail.Width / (float)image.Width, thumbnail.Height / (float)image.Height);
        float width = image.Width * ratio, height = image.Height * ratio;
        graphics.DrawImage(image, (thumbnail.Width - width) / 2, (thumbnail.Height - height) / 2, width, height);
        return thumbnail;
    }

    private void ReleaseItems()
    {
        Item[] old = list.Items.Cast<Item>().ToArray();
        list.Items.Clear();
        foreach (Item item in old) item.Thumbnail.Dispose();
    }
    private static Button Button(string text, string name, int width, ButtonRole role)
    {
        var button = new Button { Text = text, Name = name, Width = width, Height = 36, Margin = new Padding(3) };
        ButtonTheme.Apply(button, role);
        return button;
    }
    private void SetStatus(string text, bool error = false)
    { status.Text = text; status.ForeColor = error ? Color.FromArgb(166, 35, 35) : Color.FromArgb(50, 74, 82); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposedResources)
        {
            disposedResources = true;
            CancelDrag();
            formLifetime.Cancel();
            formLifetime.Dispose();
            ocrMenu.Dispose();
            editMenu.Dispose();
            tips.Dispose();
            ReleaseItems();
            Image? image = picture.Image; picture.Image = null; image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record Item(HistoryEntry Entry, Bitmap Thumbnail)
    {
        public override string ToString() => $"{Entry.CapturedAt:yyyy/MM/dd HH:mm:ss}　{Entry.Size.Width} × {Entry.Size.Height} px　"
            + (Entry.SavedPath is null ? "メモリー内のみ" : "ファイル保存済み");
    }
}
