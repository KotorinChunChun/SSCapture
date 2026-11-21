using SSCapture.Services;

namespace SSCapture.UI;

internal sealed record CaptureTimerSetup(ResolvedCaptureCommand Command, CaptureMode? SelectionMode, DisplayLayout? Display);

internal sealed class CaptureTimerForm : BrandedForm
{
    private enum Target { Range, Window, Registered, Monitor }
    private sealed record Candidate(string? Id, string Caption)
    {
        public override string ToString() => Caption;
    }

    private readonly AppSettings settings;
    private readonly IReadOnlyList<DisplayLayout> displays;
    private readonly CaptureScheduleEditor schedule = new(allowImmediate: false, allowUnlimited: true);
    private readonly ComboBox target = Combo("CaptureTimerTarget"), input = Combo("CaptureTimerInput"), output = Combo("CaptureTimerOutput");
    private readonly ComboBox registered = Combo("CaptureTimerRegistered"), monitor = Combo("CaptureTimerMonitor");
    private readonly TextBox saveDirectory = Preview("CaptureTimerSaveDirectory"), fileNamePattern = Preview("CaptureTimerFileName");
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new(530, 0) };

    internal CaptureTimerSetup? Setup { get; private set; }

    internal CaptureTimerForm(AppSettings settings, IReadOnlyList<DisplayLayout> displays) : base("タイマー撮影")
    {
        this.settings = CaptureSettingsSnapshot.Copy(settings).EnsurePresets();
        this.displays = displays;
        Name = "CaptureTimerForm";
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new(550, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, Padding = new(12) };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));

        target.Items.AddRange(["範囲を選択", "ウィンドウを選択", "登録範囲", "モニター"]);
        target.SelectedIndex = 0;
        Fill(input, "既定", this.settings.SavedRegions.Select(p => new Candidate(p.Id, p.Name)));
        Fill(output, "既定", this.settings.CapturePresets.Select(p => new Candidate(p.Id, p.Name)));
        registered.Items.AddRange(this.settings.SavedRegions.Where(p => p.UseFixedCoordinates).Cast<object>().ToArray());
        registered.DisplayMember = nameof(SavedRegion.Name);
        if (registered.Items.Count != 0) registered.SelectedIndex = 0;
        if (displays.Count != 0)
        {
            monitor.Items.AddRange(displays.Select((d, i) =>
                new Candidate(d.Identity, $"モニター{i + 1}（{d.Bounds.Width} × {d.Bounds.Height}）")).Cast<object>().ToArray());
            monitor.SelectedIndex = 0;
        }

        AddWide(schedule);
        Row("対象", target);
        Label inputLabel = Row("入力プリセット", input);
        Label registeredLabel = Row("登録範囲", registered);
        Label monitorLabel = Row("モニター", monitor);
        Row("出力プリセット", output);
        Row("保存先", saveDirectory);
        Row("命名パターン", fileNamePattern);
        AddWide(status);

        var ok = new Button { Name = "CaptureTimerReserve", Text = "予約", AutoSize = true };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([ok, cancel]);
        AddWide(buttons);
        Controls.Add(layout);
        ButtonTheme.Apply(ok, ButtonRole.Primary);
        ButtonTheme.Apply(cancel, ButtonRole.Secondary);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Confirm();

        target.SelectedIndexChanged += (_, _) => RefreshTarget();
        schedule.ScheduleChanged += (_, _) => RefreshOutput();
        output.SelectedIndexChanged += (_, _) => ShowSaveSettings();
        RefreshTarget();
        RefreshOutput();

        void RefreshTarget()
        {
            Target selected = (Target)target.SelectedIndex;
            input.Visible = inputLabel.Visible = selected != Target.Registered;
            registered.Visible = registeredLabel.Visible = selected == Target.Registered;
            monitor.Visible = monitorLabel.Visible = selected == Target.Monitor;
        }

        Label Row(string label, Control control)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new(SizeType.AutoSize));
            var name = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 6, 12, 6) };
            layout.Controls.Add(name, 0, row);
            layout.Controls.Add(control, 1, row);
            return name;
        }

        void AddWide(Control control)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }
    }

    private static ComboBox Combo(string name) => new() { Name = name, DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private static TextBox Preview(string name) => new() { Name = name, ReadOnly = true, Dock = DockStyle.Fill };

    private static void Fill(ComboBox box, string first, IEnumerable<Candidate> candidates)
    {
        box.Items.Add(new Candidate(null, first));
        box.Items.AddRange(candidates.Cast<object>().ToArray());
        box.SelectedIndex = 0;
    }

    private void RefreshOutput()
    {
        int selected = output.SelectedIndex;
        string caption = schedule.IsPeriodic ? "ファイル保存のみ（予約用）" : "既定";
        if (output.Items[0] is Candidate choice && choice.Caption != caption)
        {
            output.Items[0] = new Candidate(null, caption);
            output.SelectedIndex = selected;
        }
        ShowSaveSettings();
    }

    private void ShowSaveSettings()
    {
        string? id = (output.SelectedItem as Candidate)?.Id;
        CapturePreset preset = id is not null ? settings.CapturePresets.Single(p => p.Id == id)
            : schedule.IsPeriodic ? CaptureTimerOutput.InitialPeriodic(settings) : settings.CapturePresets.Single(p => p.IsDefault);
        bool saves = preset.Destination.HasFlag(OutputDestination.File);
        saveDirectory.Text = saves ? preset.SaveDirectory : "";
        fileNamePattern.Text = saves ? preset.FileNamePattern : "";
        saveDirectory.Enabled = fileNamePattern.Enabled = saves;
    }

    private void Confirm()
    {
        try
        {
            Target selected = (Target)target.SelectedIndex;
            string? inputId = ((Candidate)input.SelectedItem!).Id;
            if (selected == Target.Registered)
            {
                var region = registered.SelectedItem as SavedRegion ?? throw new ArgumentException("登録範囲を選んでください。");
                _ = RegionCoordinates.Resolve(region, displays);
                inputId = region.Id;
            }
            DisplayLayout? display = null;
            if (selected == Target.Monitor)
            {
                var candidate = monitor.SelectedItem as Candidate ?? throw new ArgumentException("モニターを選んでください。");
                display = displays.Single(d => d.Identity == candidate.Id);
            }
            var request = new CaptureCommandRequest(InputPresetId: inputId, OutputPresetId: ((Candidate)output.SelectedItem!).Id)
            {
                Schedule = schedule.ReadSchedule()
            };
            CaptureMode? selectionMode = selected switch { Target.Window => CaptureMode.Window, Target.Range => CaptureMode.Rectangle, _ => null };
            Setup = new(CaptureCommandLine.Resolve(request, settings), selectionMode, display);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            status.Text = ex.Message;
        }
    }
}
