using System.Drawing.Drawing2D;
using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class HistoryForm : Form
{
    private readonly CaptureHistory history;
    private readonly Func<Guid, Task<string>> copy, edit;
    private readonly ListBox list;
    private readonly PictureBox picture;
    private readonly Label details, status;
    private readonly Button copyButton, editButton, clearButton;
    private bool operating, refreshPending, disposedResources;

    public HistoryForm(CaptureHistory history, Func<Guid, Task<string>> copy, Func<Guid, Task<string>> edit)
    {
        this.history = history;
        this.copy = copy;
        this.edit = edit;
        Name = "HistoryForm";
        Text = "SSCapture — 撮影履歴";
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
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 80, Margin = new Padding(0, 0, 10, 0) };
        body.Controls.Add(list, 0, 0);
        var preview = new TableLayoutPanel { Name = "HistoryPreviewPanel", Dock = DockStyle.Fill,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        body.Controls.Add(preview, 1, 0);
        picture = new PictureBox { Name = "HistoryPreview", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(224, 229, 234), BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
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
        editButton = Button("画像編集アプリへ送る", "EditHistoryImage", 212, ButtonRole.Secondary);
        editButton.Click += async (_, _) => await SendAsync(edit);
        copyButton = Button("再コピー", "CopyHistoryImage", 124, ButtonRole.Primary);
        copyButton.Click += async (_, _) => await SendAsync(copy);
        buttons.Controls.AddRange([close, clearButton, editButton, copyButton]);
        root.Controls.Add(buttons, 0, 2);
        CancelButton = close;
        list.DrawItem += DrawEntry;
        list.SelectedIndexChanged += (_, _) => LoadPreview();
        HandleCreated += (_, _) => UpdateRowHeight();
        DpiChanged += (_, _) => UpdateRowHeight();
        RefreshEntries();
    }

    internal void RefreshEntries()
    {
        if (IsDisposed || disposedResources) return;
        if (operating) { refreshPending = true; return; }
        Guid? selected = (list.SelectedItem as Item)?.Entry.Id;
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
            int next = Enumerable.Range(0, list.Items.Count).FirstOrDefault(
                index => ((Item)list.Items[index]).Entry.Id == selected, -1);
            list.SelectedIndex = next >= 0 ? next : list.Items.Count > 0 ? 0 : -1;
            if (list.Items.Count == 0) { LoadPreview(); SetStatus("撮影履歴はありません。"); }
            else SetStatus($"{list.Items.Count}枚の履歴");
        }
        finally { list.EndUpdate(); UpdateButtons(); }
    }

    private void LoadPreview()
    {
        Image? previous = picture.Image;
        picture.Image = null; previous?.Dispose();
        if (list.SelectedItem is not Item selected) { details.Text = ""; UpdateButtons(); return; }
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
            details.Text = $"{entry.CapturedAt:yyyy/MM/dd HH:mm:ss}　{entry.Size.Width} × {entry.Size.Height} px\n"
                + (entry.SavedPath is null ? "メモリー内のみ" : "保存済み: " + entry.SavedPath);
        }
        UpdateButtons();
    }

    private async Task SendAsync(Func<Guid, Task<string>> action)
    {
        if (operating || list.SelectedItem is not Item selected) return;
        if (!history.Entries.Any(entry => entry.Id == selected.Entry.Id))
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

    private void UpdateButtons()
    {
        bool selected = list.SelectedItem is Item && picture.Image is not null;
        copyButton.Enabled = editButton.Enabled = !operating && selected;
        clearButton.Enabled = !operating && list.Items.Count > 0;
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
