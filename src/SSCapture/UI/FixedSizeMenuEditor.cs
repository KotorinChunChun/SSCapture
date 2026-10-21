namespace SSCapture.UI;

internal sealed class FixedSizeMenuEditor : UserControl
{
    private readonly NumericUpDown width, height;
    internal FixedSizeMenuEditor(Size size, Action<Size> apply)
    {
        Name = "FixedSizeMenuEditor"; Size = new Size(340, 126); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        width = new NumericUpDown { Name = "CaptureFixedWidth", Minimum = 1, Maximum = 32768, Dock = DockStyle.Fill, AccessibleName = "固定幅 (px)" };
        height = new NumericUpDown { Name = "CaptureFixedHeight", Minimum = 1, Maximum = 32768, Dock = DockStyle.Fill, AccessibleName = "固定高さ (px)" };
        layout.Controls.Add(width, 0, 0); layout.Controls.Add(height, 1, 0);
        var button = SettingsForm.MakeButton("この寸法を適用", "ApplyCaptureFixedSize"); button.Dock = DockStyle.Fill;
        layout.Controls.Add(button, 0, 1); layout.SetColumnSpan(button, 2);
        var status = new Label { Dock = DockStyle.Fill, Text = "左: 幅 / 右: 高さ（物理px）", AutoEllipsis = true };
        layout.Controls.Add(status, 0, 2); layout.SetColumnSpan(status, 2);
        button.Click += (_, _) =>
        {
            try { var next = new Size((int)width.Value, (int)height.Value); SSCapture.Capture.CaptureGeometry.ValidateFixedSize(next); apply(next); }
            catch (ArgumentException ex) { status.Text = ex.Message; }
        };
        SetSize(size);
    }
    internal void SetSize(Size size) { width.Value = Math.Clamp(size.Width, 1, 32768); height.Value = Math.Clamp(size.Height, 1, 32768); }
}
