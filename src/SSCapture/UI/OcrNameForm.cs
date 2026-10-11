using SSCapture.Services;
using System.Globalization;

namespace SSCapture.UI;

internal sealed class OcrNameForm : Form
{
    private readonly Bitmap image;
    private readonly AppSettings settings;
    private readonly CaptureIntent intent;
    private readonly DateTimeOffset capturedAt;
    private readonly ListBox candidates;
    private readonly TextBox[] selected = new TextBox[3];
    private readonly TextBox separator, pattern, date, time;
    private readonly CheckBox openFolder;
    private readonly Label status, preview;
    private readonly Button confirm, add;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<Bitmap, string, CancellationToken, Task<OcrResult>> recognize;
    private bool resourcesDisposed;
    private bool confirmationStarted;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string? FileName { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal FileNamePlan? NamePlan { get; private set; }
    internal AppSettings OutputSettings => settings with { FileNamePattern = pattern.Text, OcrSeparator = separator.Text, OpenFolderAfterSave = openFolder.Checked };

    internal OcrNameForm(Bitmap source, AppSettings settings, CaptureIntent intent, DateTimeOffset capturedAt,
        Func<Bitmap, string, CancellationToken, Task<OcrResult>>? recognize = null)
    {
        image = (Bitmap)source.Clone(); this.settings = settings; this.intent = intent; this.capturedAt = capturedAt;
        this.recognize = recognize ?? OneOcr.RecognizeAsync;
        Text = "SSCapture — OCRファイル名"; Name = "OcrNameForm";
        MinimizeBox = false; MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Yu Gothic UI", 12F);
        ClientSize = new Size(980, 712); MinimumSize = new Size(860, 702);
        StartPosition = FormStartPosition.CenterScreen; Icon = AppIcon.Create();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 0f, 126f, 40f, 40f, 40f, 32f, 44f, 44f, 38f })
            layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        Controls.Add(layout);
        var upper = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        upper.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); upper.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var picture = new PictureBox { Name = "OcrImage", Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.FromArgb(235, 238, 240) };
        upper.Controls.Add(picture, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        candidates = new ListBox { Name = "OcrCandidates", Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        right.Controls.Add(candidates, 0, 0);
        add = SettingsForm.MakeButton("選択行を追加", "AddOcrLine", ButtonRole.Secondary); add.Dock = DockStyle.Fill; add.Enabled = false; add.Margin = Padding.Empty;
        add.Click += (_, _) => AddCandidate(); candidates.DoubleClick += (_, _) => AddCandidate();
        right.Controls.Add(add, 0, 1); upper.Controls.Add(right, 1, 0); layout.Controls.Add(upper, 0, 0);
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = Padding.Empty };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        for (int index = 0; index < 3; index++)
        {
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            rows.Controls.Add(new Label { Text = $"文字列 {index + 1}", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, index);
            var edit = new TextBox { Name = $"OcrText{index + 1}", Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 0), MaxLength = 240 };
            selected[index] = edit; rows.Controls.Add(edit, 1, index);
            var clear = SettingsForm.MakeButton("消去", $"ClearOcrText{index + 1}", ButtonRole.Danger); clear.Dock = DockStyle.Fill; clear.Margin = new Padding(3, 3, 0, 3);
            clear.Click += (_, _) => edit.Clear(); rows.Controls.Add(clear, 2, index);
        }
        layout.Controls.Add(rows, 0, 1);
        var delimiter = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        delimiter.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); delimiter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        delimiter.Controls.Add(new Label { Text = "区切り", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        separator = new TextBox { Name = "OcrSeparator", Text = settings.OcrSeparator, Dock = DockStyle.Fill, MaxLength = 16 };
        delimiter.Controls.Add(separator, 1, 0); layout.Controls.Add(delimiter, 0, 2);
        var naming = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        naming.Controls.Add(new Label { Text = "パターン", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        pattern = new TextBox { Name = "OcrFileNamePattern", Text = settings.FileNamePattern, Dock = DockStyle.Fill };
        naming.Controls.Add(pattern, 1, 0); layout.Controls.Add(naming, 0, 3);
        var timestamp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        timestamp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); timestamp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        timestamp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); timestamp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        timestamp.Controls.Add(new Label { Text = "年月日", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        date = new TextBox { Name = "OcrDate", Text = capturedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Dock = DockStyle.Fill, MaxLength = 8 };
        time = new TextBox { Name = "OcrTime", Text = capturedAt.ToString("HHmmss", CultureInfo.InvariantCulture), Dock = DockStyle.Fill, MaxLength = 6 };
        timestamp.Controls.Add(date, 1, 0);
        timestamp.Controls.Add(new Label { Text = "時分秒", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        timestamp.Controls.Add(time, 3, 0); layout.Controls.Add(timestamp, 0, 4);
        openFolder = new CheckBox { Name = "OcrOpenFolderAfterSave", Text = "保存後に保存フォルダを開く", Checked = settings.OpenFolderAfterSave, Dock = DockStyle.Fill };
        layout.Controls.Add(openFolder, 0, 5);
        preview = new Label { Name = "FileNamePreview", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        status = new Label { Name = "OcrStatus", Dock = DockStyle.Fill, AutoEllipsis = true, Text = "OneOCRで認識中…" };
        layout.Controls.Add(preview, 0, 6); layout.Controls.Add(status, 0, 7);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        confirm = SettingsForm.MakeButton("確定 (Ctrl+Enter)", "ConfirmOcrName", ButtonRole.Primary); confirm.Width = 220;
        confirm.Click += (_, _) => ConfirmName();
        var cancel = SettingsForm.MakeButton("キャンセル", "CancelOcrName", ButtonRole.Neutral); cancel.Width = 120;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.AddRange([confirm, cancel]); layout.Controls.Add(buttons, 0, 8);
        CancelButton = cancel;
        foreach (var edit in selected) edit.TextChanged += (_, _) => RefreshName();
        foreach (var edit in new[] { separator, pattern, date, time }) edit.TextChanged += (_, _) => RefreshName();
        RefreshName();
        Shown += async (_, _) => await RecognizeAsync();
        FormClosed += (_, _) => lifetime.Cancel();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            ConfirmName();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ConfirmName()
    {
        if (confirmationStarted || resourcesDisposed || IsDisposed || Disposing
            || DialogResult != DialogResult.None || !confirm.Enabled) return;
        confirmationStarted = true;
        try
        {
            FileNamePlan plan = ResolvePlan();
            string fileName = plan.Candidate(0);
            NamePlan = plan;
            FileName = fileName;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException ex)
        {
            confirmationStarted = false;
            status.Text = ex.Message;
        }
    }

    private string ResolveName() => ResolvePlan().Candidate(0);
    private FileNamePlan ResolvePlan()
    {
        string text = "";
        if (FileNamePattern.UsesOcr(pattern.Text))
        {
            text = FileNamePattern.JoinOcr(selected.Select(edit => edit.Text), separator.Text);
        }
        DateOnly day = DateOnly.FromDateTime(capturedAt.DateTime);
        TimeOnly clock = TimeOnly.FromDateTime(capturedAt.DateTime);
        if (pattern.Text.Contains("{yyyymmdd}", StringComparison.Ordinal) && !DateOnly.TryParseExact(date.Text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            throw new ArgumentException("年月日はyyyyMMdd形式の有効な8桁で入力してください。");
        if (pattern.Text.Contains("{hhmmss}", StringComparison.Ordinal) && !TimeOnly.TryParseExact(time.Text, "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out clock))
            throw new ArgumentException("時分秒はHHmmss形式の有効な6桁で入力してください。");
        return new FileNamePlan(pattern.Text, new DateTimeOffset(day.ToDateTime(clock), capturedAt.Offset), intent, text);
    }
    private void RefreshName()
    {
        try { preview.Text = ResolveName(); confirm.Enabled = true; }
        catch (ArgumentException ex) { preview.Text = ex.Message; confirm.Enabled = false; }
    }
    private void AddCandidate()
    {
        if (candidates.SelectedItem is not string line) return;
        var empty = selected.FirstOrDefault(edit => string.IsNullOrWhiteSpace(edit.Text));
        if (empty is null) { status.Text = "文字列は3個までです。編集または消去してください。"; return; }
        empty.Text = line;
    }
    private async Task RecognizeAsync()
    {
        // Own a separate image until the worker has consumed it, even if the form closes.
        using var copy = (Bitmap)image.Clone();
        CancellationToken token = lifetime.Token;
        try
        {
            OcrResult result = await Task.Run(() => recognize(copy, settings.OneOcrDirectory, token));
            if (IsDisposed || token.IsCancellationRequested) return;
            candidates.Items.AddRange(result.Lines.Cast<object>().ToArray());
            add.Enabled = result.Lines.Length > 0;
            if (add.Enabled) candidates.SelectedIndex = 0;
            status.Text = result.Error is not null ? "認識できません: " + result.Error + " 文字列は手入力できます。" :
                result.Lines.Length == 0 ? "文字を認識できませんでした。文字列を手入力できます。" : $"{result.Lines.Length}行認識しました。最大3個を選択し、編集できます。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) status.Text = "認識できません: " + ex.Message; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; lifetime.Cancel(); lifetime.Dispose(); image.Dispose(); }
        base.Dispose(disposing);
    }
}
