namespace SSCapture.UI;
internal sealed class CapturePrecisionEditor : UserControl
{
    private readonly NumericUpDown distance, step;
    internal CapturePrecisionEditor(int distancePixels, int stepPixels, Action<int, int> apply)
    {
        Size = new(420, 130); AutoScaleMode = AutoScaleMode.Dpi;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new(6) }; Controls.Add(table);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int row = 0; row < 3; row++) table.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        table.Controls.Add(new Label { Text = "吸着距離 (px)", Dock = DockStyle.Fill }, 0, 0); table.Controls.Add(new Label { Text = "刻み幅 (px)", Dock = DockStyle.Fill }, 1, 0);
        distance = new NumericUpDown { Name = "MenuSnapDistance", Minimum = 1, Maximum = 32768, Dock = DockStyle.Fill }; step = new NumericUpDown { Name = "MenuGridStep", Minimum = 1, Maximum = 32768, Dock = DockStyle.Fill };
        table.Controls.Add(distance, 0, 1); table.Controls.Add(step, 1, 1);
        var button = SettingsForm.MakeButton("今回の精度を適用", "ApplyMenuPrecision"); button.Dock = DockStyle.Fill; table.Controls.Add(button, 0, 2); table.SetColumnSpan(button, 2);
        button.Click += (_, _) => apply((int)distance.Value, (int)step.Value); SetValues(distancePixels, stepPixels);
    }
    internal void SetValues(int distancePixels, int stepPixels) { distance.Value = distancePixels; step.Value = stepPixels; }
}
