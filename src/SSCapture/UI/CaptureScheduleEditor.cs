using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CaptureScheduleEditor : TableLayoutPanel
{
    private enum Mode { Immediate, Delay, At, Periodic }
    private sealed record Choice(Mode Mode, string Caption)
    {
        public override string ToString() => Caption;
    }

    private readonly ComboBox mode = new() { Name = "CaptureScheduleMode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown seconds = Number("CaptureScheduleSeconds", 5);
    private readonly NumericUpDown interval = Number("CaptureScheduleInterval", 10);
    private readonly NumericUpDown count = Number("CaptureScheduleCount", 3);
    private readonly DateTimePicker at = new()
    {
        Name = "CaptureScheduleAt", Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy/MM/dd HH:mm:ss",
        Dock = DockStyle.Fill, Value = DateTime.Now.AddMinutes(1)
    };
    private readonly CheckBox unlimited = new() { Name = "CaptureScheduleUnlimited", Text = "停止まで", AutoSize = true };
    private readonly Control[] labels = new Control[5];

    internal event EventHandler? ScheduleChanged;
    internal bool IsPeriodic => SelectedMode == Mode.Periodic;
    private Mode SelectedMode => ((Choice)mode.SelectedItem!).Mode;

    internal CaptureScheduleEditor(bool allowImmediate, bool allowUnlimited)
    {
        Name = "CaptureSchedule";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        ColumnCount = 2;
        ColumnStyles.Add(new(SizeType.AutoSize));
        ColumnStyles.Add(new(SizeType.Percent, 100));
        if (allowImmediate) mode.Items.Add(new Choice(Mode.Immediate, "すぐに撮影"));
        mode.Items.AddRange([new Choice(Mode.Delay, "指定秒数後"), new Choice(Mode.At, "指定日時"), new Choice(Mode.Periodic, "定期撮影")]);
        mode.SelectedIndex = 0;
        for (int row = 0; row < 5; row++) RowStyles.Add(new(SizeType.AutoSize));
        Row("タイマー", mode, 0);
        Row("待ち時間（秒）", seconds, 1);
        Row("日時", at, 2);
        Row("間隔（秒）", interval, 3);
        var countControls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        count.Width = 96;
        countControls.Controls.Add(count);
        if (allowUnlimited) countControls.Controls.Add(unlimited);
        Row("撮影枚数", countControls, 4);

        mode.SelectedIndexChanged += (_, _) => Changed();
        foreach (var field in new[] { seconds, interval, count }) field.ValueChanged += (_, _) => Changed();
        at.ValueChanged += (_, _) => Changed();
        unlimited.CheckedChanged += (_, _) => Changed();
        Changed();
    }

    private static NumericUpDown Number(string name, int value) => new()
    {
        Name = name, Maximum = int.MaxValue, Value = value, Dock = DockStyle.Fill
    };

    private void Row(string text, Control value, int index)
    {
        var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 6, 12, 6) };
        labels[index] = label;
        label.MinimumSize = label.GetPreferredSize(Size.Empty);
        label.FontChanged += (_, _) => label.MinimumSize = label.GetPreferredSize(Size.Empty);
        Controls.Add(label, 0, index);
        Controls.Add(value, 1, index);
    }

    private void Changed()
    {
        RowVisible(1, SelectedMode == Mode.Delay);
        RowVisible(2, SelectedMode == Mode.At);
        RowVisible(3, IsPeriodic);
        RowVisible(4, IsPeriodic);
        count.Enabled = !unlimited.Checked;
        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RowVisible(int row, bool visible)
    {
        labels[row].Visible = visible;
        GetControlFromPosition(1, row)!.Visible = visible;
    }

    internal CaptureSchedule? ReadSchedule()
    {
        CaptureSchedule? schedule = SelectedMode switch
        {
            Mode.Delay => new CaptureSchedule.Delay((int)seconds.Value),
            Mode.At => CaptureTimerArguments.LocalTime(at.Value),
            Mode.Periodic => new CaptureSchedule.Periodic((int)interval.Value, unlimited.Checked ? null : (int)count.Value),
            _ => null
        };
        schedule?.Validate(DateTimeOffset.Now);
        return schedule;
    }
}
