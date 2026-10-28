using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CaptureCommandControl : UserControl
{
    private sealed record PresetChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly Func<AppSettings> readSettings;
    private readonly Action<string, bool> report;
    private readonly string executable;
    private readonly Func<string, Task> copyCommand;
    private readonly ComboBox method = new() { Name = "CaptureCommandMethod", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox input = new() { Name = "CaptureCommandInput", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox output = new() { Name = "CaptureCommandOutput", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown x = Coordinate("CaptureCommandX"), y = Coordinate("CaptureCommandY");
    private readonly NumericUpDown width = Dimension("CaptureCommandWidth"), height = Dimension("CaptureCommandHeight");
    private readonly Button import = new() { Name = "CaptureCommandImport", Text = "範囲を取り込む", AutoSize = true };
    private readonly Button generate = new() { Name = "CaptureCommandGenerate", Text = "生成", AutoSize = true };
    private readonly Button copy = new() { Name = "CaptureCommandCopy", Text = "コピー", AutoSize = true, Enabled = false };
    private readonly TextBox command = new()
    {
        Name = "CaptureCommandText", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill, MinimumSize = new(0, 130), AccessibleName = "PowerShellコマンド"
    };
    private bool refreshing;

    internal CaptureCommandControl(Func<AppSettings> readSettings, Action<string, bool> report, string? executable = null, Func<string, Task>? copyCommand = null)
    {
        this.readSettings = readSettings;
        this.report = report;
        this.executable = executable ?? Path.Combine(AppContext.BaseDirectory, "SSCapture.exe");
        this.copyCommand = copyCommand ?? (text => { Clipboard.SetText(text); return Task.CompletedTask; });
        Name = "CaptureCommandControl";
        AccessibleName = "撮影コマンド";
        Dock = DockStyle.Fill;
        AutoScroll = true;
        method.Items.AddRange(["範囲選択", "固定範囲"]);
        method.SelectedIndex = 0;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 10, Padding = new(8) };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string label, Control value, int row)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 8, 12, 8) }, 0, row);
            layout.Controls.Add(value, 1, row);
        }
        Row("方式", method, 0);
        Row("入力プリセット", input, 1);
        Row("出力プリセット", output, 2);
        Row("X", x, 3); Row("Y", y, 4); Row("幅", width, 5); Row("高さ", height, 6);
        layout.Controls.Add(import, 1, 7);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        buttons.Controls.AddRange([generate, copy]);
        layout.Controls.Add(buttons, 1, 8);
        layout.Controls.Add(command, 0, 9); layout.SetColumnSpan(command, 2);
        Controls.Add(layout);
        ButtonTheme.Apply(generate, ButtonRole.Primary);
        ButtonTheme.Apply(copy, ButtonRole.Secondary);
        ButtonTheme.Apply(import, ButtonRole.Secondary);
        method.SelectedIndexChanged += (_, _) => { UpdateCoordinates(); ClearCommand(); };
        input.SelectedIndexChanged += (_, _) => { UpdateCoordinates(); if (!refreshing) ClearCommand(); };
        output.SelectedIndexChanged += (_, _) => { if (!refreshing) ClearCommand(); };
        foreach (var number in new[] { x, y, width, height }) number.ValueChanged += (_, _) => ClearCommand();
        import.Click += (_, _) => ImportRegion();
        generate.Click += (_, _) => Generate();
        copy.Click += async (_, _) => await CopyAsync();
        VisibleChanged += (_, _) => { if (Visible) RefreshCandidates(); };
        RefreshCandidates();
        UpdateCoordinates();
    }

    private static NumericUpDown Coordinate(string name) => new() { Name = name, Minimum = int.MinValue, Maximum = int.MaxValue, Dock = DockStyle.Fill };
    private static NumericUpDown Dimension(string name) => new() { Name = name, Minimum = 1, Maximum = RegionCoordinates.MaximumDimension, Value = 1, Dock = DockStyle.Fill };

    internal void RefreshCandidates()
    {
        try
        {
            AppSettings settings = readSettings();
            refreshing = true;
            RefreshList(input, settings.SavedRegions.Select(p => new PresetChoice(p.Id, p.Name)));
            RefreshList(output, settings.CapturePresets.Select(p => new PresetChoice(p.Id, p.Name)));
            ClearCommand();
        }
        catch (Exception ex)
        {
            input.SelectedIndex = output.SelectedIndex = -1;
            ClearCommand();
            report("プリセットを更新できません: " + ex.Message, true);
        }
        finally { refreshing = false; UpdateCoordinates(); }
    }

    private static void RefreshList(ComboBox box, IEnumerable<PresetChoice> choices)
    {
        string? selected = (box.SelectedItem as PresetChoice)?.Id;
        bool missing = box.Items.Count != 0 && box.SelectedIndex < 0;
        box.BeginUpdate();
        try
        {
            box.Items.Clear();
            box.Items.Add(new PresetChoice(null, "既定"));
            box.Items.AddRange(choices.Cast<object>().ToArray());
            box.SelectedIndex = missing ? -1 : Enumerable.Range(0, box.Items.Count)
                .FirstOrDefault(index => box.Items[index] is PresetChoice choice && choice.Id == selected, -1);
        }
        finally { box.EndUpdate(); }
    }

    private void UpdateCoordinates()
    {
        bool fixedRegion = method.SelectedIndex == 1;
        foreach (var number in new[] { x, y, width, height }) number.Enabled = fixedRegion;
        import.Enabled = fixedRegion && input.SelectedIndex >= 0;
    }

    private void ClearCommand() { command.Clear(); copy.Enabled = false; }

    private CaptureCommandRequest Request()
    {
        if (input.SelectedItem is not PresetChoice selectedInput || output.SelectedItem is not PresetChoice selectedOutput)
            throw new ArgumentException("入力・出力プリセットを選び直してください。");
        return new(method.SelectedIndex == 1 ? new Rectangle((int)x.Value, (int)y.Value, (int)width.Value, (int)height.Value) : null,
            selectedInput.Id, selectedOutput.Id);
    }

    private void ImportRegion()
    {
        try
        {
            var request = Request();
            AppSettings settings = readSettings();
            SavedRegion? preset = request.InputPresetId is null ? settings.SavedRegions.SingleOrDefault(p => p.IsDefault)
                : settings.SavedRegions.SingleOrDefault(p => p.Id == request.InputPresetId);
            if (preset is null) throw new ArgumentException("入力プリセットを選び直してください。");
            preset.Validate();
            Rectangle bounds = preset.UseFixedCoordinates ? RegionCoordinates.Resolve(preset, RegionCoordinates.ReadDisplays()) : preset.Bounds;
            CaptureCommandLine.ValidateRegion(bounds);
            x.Value = bounds.X; y.Value = bounds.Y; width.Value = bounds.Width; height.Value = bounds.Height;
        }
        catch (Exception ex) { ClearCommand(); report("範囲を取り込めません: " + ex.Message, true); }
    }

    private void Generate()
    {
        try
        {
            var request = Request();
            _ = CaptureCommandLine.Resolve(request, readSettings());
            command.Text = CaptureCommandLine.Generate(executable, request);
            copy.Enabled = true;
        }
        catch (Exception ex) { ClearCommand(); report("コマンドを生成できません: " + ex.Message, true); }
    }

    private async Task CopyAsync()
    {
        if (command.TextLength == 0) return;
        string text = command.Text;
        copy.Enabled = false;
        try { await copyCommand(text); if (!IsDisposed) report("コマンドをコピーしました。", false); }
        catch (Exception ex) { if (!IsDisposed) report("コピーできません: " + ex.Message, true); }
        finally { if (!IsDisposed) copy.Enabled = command.TextLength > 0; }
    }

}
