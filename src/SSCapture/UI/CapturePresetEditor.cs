using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CapturePresetEditor : UserControl
{
    private readonly ListBox list;
    private readonly TextBox presetName, directory, pattern;
    private readonly NumericUpDown padding;
    private readonly CheckBox clipboard, file, external;
    private readonly Label status;
    private readonly Func<AppSettings> readSettings;
    internal CapturePreset[] Presets => list.Items.Cast<CapturePreset>().ToArray();

    internal CapturePresetEditor(AppSettings initial, Func<AppSettings> readSettings)
    {
        this.readSettings = readSettings;
        Name = "CapturePresetEditor"; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);
        list = new ListBox { Name = "CapturePresetList", Dock = DockStyle.Fill, IntegralHeight = false };
        root.Controls.Add(list, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(fields, 1, 0);
        presetName = new TextBox { Name = "CapturePresetName", MaxLength = 80 }; Add("名前", presetName, 0);
        var outputs = new FlowLayoutPanel { WrapContents = false };
        clipboard = Toggle("コピー", "PresetClipboard"); file = Toggle("ファイル", "PresetFile"); external = Toggle("外部アプリ", "PresetExternal");
        outputs.Controls.AddRange([clipboard, file, external]); Add("出力先", outputs, 1);
        padding = new NumericUpDown { Name = "PresetPaddingPixels", Minimum = 0, Maximum = ScreenshotPadding.MaximumPixels };
        Add("白余白 (px)", padding, 2);
        directory = new TextBox { Name = "PresetSaveDirectory" }; Add("保存フォルダー", directory, 3);
        pattern = new TextBox { Name = "PresetFileNamePattern" }; Add("命名パターン", pattern, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        Button Button(string text, string name, int width, Action action)
        {
            var button = SettingsForm.MakeButton(text, name); button.Width = width;
            button.Click += (_, _) => action(); buttons.Controls.Add(button); return button;
        }
        Button("新規", "NewCapturePreset", 100, () => { list.ClearSelected(); Fill(CapturePreset.FromSettings("", readSettings())); });
        Button("登録・更新", "StoreCapturePreset", 136, StoreCurrent);
        Button("削除", "DeleteCapturePreset", 100, () => { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); });
        Button("現在の基本設定を読込", "ReadCapturePreset", 232, () => Fill(CapturePreset.FromSettings(presetName.Text, readSettings())));
        root.Controls.Add(buttons, 0, 1); root.SetColumnSpan(buttons, 2);
        status = new Label { Name = "PresetEditorStatus", Dock = DockStyle.Fill, Text = "登録・更新後、設定画面の「保存」で確定します。撮影中の適用は今回だけです。" };
        root.Controls.Add(status, 0, 2); root.SetColumnSpan(status, 2);
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is CapturePreset preset) Fill(preset); };
        foreach (var preset in initial.CapturePresets) list.Items.Add(preset);
        Fill(CapturePreset.FromSettings("", initial));

        void Add(string text, Control input, int row)
        {
            fields.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(3, 4, 0, 3); fields.Controls.Add(input, 1, row);
        }
        static CheckBox Toggle(string text, string name) => new() { Text = text, Name = name, AutoSize = true };
    }

    private void Fill(CapturePreset preset)
    {
        presetName.Text = preset.Name; directory.Text = preset.SaveDirectory; pattern.Text = preset.FileNamePattern;
        padding.Value = preset.PaddingPixels;
        clipboard.Checked = preset.Destination.HasFlag(OutputDestination.Clipboard);
        file.Checked = preset.Destination.HasFlag(OutputDestination.File);
        external.Checked = preset.Destination.HasFlag(OutputDestination.ExternalApp);
    }

    internal void StoreCurrent()
    {
        try
        {
            var preset = new CapturePreset { Id = (list.SelectedItem as CapturePreset)?.Id ?? Guid.NewGuid().ToString("N"),
                Name = presetName.Text.Trim(), SaveDirectory = Environment.ExpandEnvironmentVariables(directory.Text.Trim().Trim('"')),
                FileNamePattern = pattern.Text, PaddingPixels = (int)padding.Value,
                Destination = (clipboard.Checked ? OutputDestination.Clipboard : 0) | (file.Checked ? OutputDestination.File : 0) | (external.Checked ? OutputDestination.ExternalApp : 0) };
            preset.Validate();
            var next = Presets.Where(p => p.Id != preset.Id).Append(preset).ToArray();
            new AppSettings { CapturePresets = next }.Validate();
            if (list.SelectedIndex >= 0) { int selected = list.SelectedIndex; list.Items[selected] = preset; list.SelectedIndex = selected; }
            else { list.Items.Add(preset); list.SelectedIndex = list.Items.Count - 1; }
            status.Text = "登録・更新しました。設定画面の「保存」で確定します。";
            status.ForeColor = Color.FromArgb(28, 37, 47);
        }
        catch (ArgumentException error) { status.Text = error.Message; status.ForeColor = Color.DarkRed; }
    }
}
