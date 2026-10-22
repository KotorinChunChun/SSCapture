using SSCapture.Services;
namespace SSCapture.UI;
internal sealed class CaptureOutputMenuEditor : UserControl
{
    private readonly TextBox directory, pattern, separator;
    internal CaptureOutputMenuEditor(AppSettings settings, Action<string, string, string> apply)
    {
        Size = new(550, 225); AutoScaleMode = AutoScaleMode.Dpi;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new(6) }; Controls.Add(table);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 6; row++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 16.67f));
        directory = Field("保存先", "MenuSaveDirectory", 0); pattern = Field("命名パターン", "MenuFileNamePattern", 1); separator = Field("OCR区切り", "MenuOcrSeparator", 2);
        var help = new Label { Text = "{ext}: png/mp4　{ocr}: OCR文字列　{#}/{##}: 連番", Dock = DockStyle.Fill }; table.Controls.Add(help, 0, 3); table.SetColumnSpan(help, 2);
        var button = SettingsForm.MakeButton("今回の出力へ適用", "ApplyMenuOutput"); button.Dock = DockStyle.Fill; table.Controls.Add(button, 0, 4); table.SetColumnSpan(button, 2);
        var status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true }; table.Controls.Add(status, 0, 5); table.SetColumnSpan(status, 2);
        button.Click += (_, _) => { try { apply(directory.Text, pattern.Text, separator.Text); status.Text = "適用しました。"; } catch (ArgumentException ex) { status.Text = ex.Message; } }; SetValues(settings);
        TextBox Field(string label, string name, int row) { table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill }, 0, row); var text = new TextBox { Name = name, Dock = DockStyle.Fill }; table.Controls.Add(text, 1, row); return text; }
    }
    internal void SetValues(AppSettings settings) { directory.Text = settings.SaveDirectory; pattern.Text = settings.FileNamePattern; separator.Text = settings.OcrSeparator; }
}
