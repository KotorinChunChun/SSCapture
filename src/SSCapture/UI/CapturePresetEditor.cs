using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CapturePresetEditor : UserControl
{
    private readonly ListBox list;
    private readonly TextBox presetName, directory, pattern, separator;
    private readonly CheckBox clipboard, file, external, openFolder;
    private readonly ComboBox imageApp, videoApp, metadataMode, color;
    private readonly Label status;
    private readonly Func<AppSettings> readSettings;
    private readonly AppSettings initialSettings;
    private readonly TableLayoutPanel naming;
    internal CapturePreset[] Presets => list.Items.Cast<CapturePreset>().ToArray();
    internal bool SelectDefault()
    {
        if (list.SelectedItem is CapturePreset { IsDefault: true }) return true;
        StoreCurrent(); if (Error is not null) return false;
        int index = Array.FindIndex(Presets, p => p.IsDefault);
        if (index < 0) return false;
        list.SelectedIndex = index; return true;
    }
    internal string? Error { get; private set; }
    internal CapturePresetEditor(AppSettings initial, Func<AppSettings> readSettings)
    {
        this.readSettings = readSettings; initialSettings = initial;
        Name = "CapturePresetEditor"; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); Controls.Add(root);
        list = new ListBox { Name = "CapturePresetList", Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
        list.Format += (_, e) => { if (e.ListItem is CapturePreset p) e.Value = (p.IsDefault ? "★ " : "") + p.Name; }; root.Controls.Add(list, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.Controls.Add(right, 1, 0);
        var header = Fields(); header.RowCount = 1; presetName = new TextBox { Name = "CapturePresetName", MaxLength = 80 }; Add(header, "名前", presetName, 0); right.Controls.Add(header, 0, 0);
        var tabs = new TabControl { Name = "OutputPresetSections", Dock = DockStyle.Fill }; right.Controls.Add(tabs, 0, 1);
        var output = Page("出力・画像"); naming = Page("保存・OCR"); var metadata = Page("撮影情報");
        var outputs = new FlowLayoutPanel { WrapContents = false, AutoSize = true };
        clipboard = Toggle("コピー", "ClipboardOutput"); file = Toggle("ファイル", "FileOutput"); external = Toggle("外部アプリ", "ExternalOutput"); outputs.Controls.AddRange([clipboard, file, external]); Add(output, "出力先", outputs, 0);
        color = new ComboBox { Name = "ScreenshotColorDepth", DropDownStyle = ComboBoxStyle.DropDownList }; color.Items.AddRange(["原色を維持（減色なし）", "256色（8ビット）", "16色（4ビット）"]); Add(output, "静止画の減色", color, 1);
        imageApp = new ComboBox { Name = "PresetImageEditor", DropDownStyle = ComboBoxStyle.DropDownList }; Add(output, "画像の外部アプリ", imageApp, 2);
        videoApp = new ComboBox { Name = "PresetVideoEditor", DropDownStyle = ComboBoxStyle.DropDownList }; Add(output, "動画の外部アプリ", videoApp, 3);
        directory = new TextBox { Name = "SaveDirectory" }; Add(naming, "保存フォルダー", directory, 0);
        pattern = new TextBox { Name = "FileNamePattern" }; Add(naming, "命名パターン", pattern, 1);
        separator = new TextBox { Name = "OcrSeparator" }; Add(naming, "OCR区切り", separator, 2);
        openFolder = Toggle("保存フォルダを開く", "OpenFolderAfterSave"); Add(naming, "保存後", openFolder, 3);
        var help = new Label { Text = "{yyyymmdd} 年月日 / {hhmmss} 時分秒 / {ext} png・mp4\n{ocr} OCR文字列 / {region} 範囲名 / {#}・{##} 連番", Dock = DockStyle.Fill }; naming.Controls.Add(help, 0, 5); naming.SetColumnSpan(help, 2); naming.SetRowSpan(help, 2);
        metadataMode = new ComboBox { Name = "MetadataMode", DropDownStyle = ComboBoxStyle.DropDownList }; metadataMode.Items.AddRange(["記録しない", "PNG内XMP", "別JSON", "PNG内XMPと別JSON"]); Add(metadata, "保存形式", metadataMode, 0);
        var warning = new Label { Dock = DockStyle.Fill, Text = "静止画のファイル保存時に座標・UIA・exe/class/titleを記録します。\n共有時はウィンドウ名やパスにご注意ください。\nパスワードの名前・値の本文・UI全体のツリーは採取しません。\n画像とUIAは同時刻ではなく、未取得時は理由を記録します。" }; metadata.Controls.Add(warning, 0, 1); metadata.SetColumnSpan(warning, 2); metadata.SetRowSpan(warning, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; root.Controls.Add(buttons, 0, 1); root.SetColumnSpan(buttons, 2);
        Button("新規", "NewCapturePreset", () => { list.ClearSelected(); Fill(CapturePreset.FromSettings("", CurrentSettings())); });
        Button("複製", "DuplicateCapturePreset", () => { var source = ReadCurrent(); list.ClearSelected(); Fill(source with { Name = source.Name + "のコピー", IsDefault = false }); });
        Button("保存", "StoreCapturePreset", StoreCurrent);
        Button("削除", "DeleteCapturePreset", () => { if (list.SelectedItem is CapturePreset p) { if (p.IsDefault) { ShowError("別のプリセットを既定にしてから削除してください。"); return; } list.Items.RemoveAt(list.SelectedIndex); } });
        Button("既定にする", "DefaultCapturePreset", () => { StoreCurrent(); if (Error is not null || list.SelectedItem is not CapturePreset p) return; var all = Presets.Select(x => x with { IsDefault = x.Id == p.Id }).ToArray(); list.Items.Clear(); list.Items.AddRange(all); list.SelectedIndex = Array.FindIndex(all, x => x.Id == p.Id); });
        status = new Label { Name = "PresetEditorStatus", Dock = DockStyle.Fill, Text = "編集内容は設定画面の「保存」で確定します。" }; root.Controls.Add(status, 0, 2); root.SetColumnSpan(status, 2);
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is CapturePreset p) Fill(p); };
        foreach (var preset in initial.CapturePresets) list.Items.Add(preset);
        if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, Array.FindIndex(Presets, p => p.IsDefault)); else Fill(CapturePreset.FromSettings("", initial));
        file.CheckedChanged += (_, _) => UpdateFileFields(); UpdateFileFields();
        imageApp.Enter += (_, _) => RefreshApps(imageApp, CurrentSettings().ImageEditors, (imageApp.SelectedItem as ExternalApplication)?.Id);
        videoApp.Enter += (_, _) => RefreshApps(videoApp, CurrentSettings().VideoEditors, (videoApp.SelectedItem as ExternalApplication)?.Id);
        TableLayoutPanel Page(string title) { var page = new TabPage(title); var fields = Fields(); page.Controls.Add(fields); tabs.TabPages.Add(page); return fields; }
        void Button(string text, string name, Action action) { var b = SettingsForm.MakeButton(text, name); b.Width = 110; b.Click += (_, _) => { try { action(); } catch (ArgumentException ex) { ShowError(ex.Message); } }; buttons.Controls.Add(b); }
    }
    private AppSettings CurrentSettings() => IsHandleCreated ? readSettings() : initialSettings;
    internal void AddSharedOcr(Control control) => Add(naming, "OCRエンジン（共通）", control, 4);
    private void UpdateFileFields() { metadataMode.Enabled = directory.Enabled = pattern.Enabled = separator.Enabled = openFolder.Enabled = file.Checked; }
    private void Fill(CapturePreset p)
    {
        RefreshApps(imageApp, CurrentSettings().ImageEditors, p.ImageEditorId); RefreshApps(videoApp, CurrentSettings().VideoEditors, p.VideoEditorId);
        presetName.Text = p.Name; directory.Text = p.SaveDirectory; pattern.Text = p.FileNamePattern; separator.Text = p.OcrSeparator; color.SelectedIndex = (int)p.ColorDepth; openFolder.Checked = p.OpenFolderAfterSave; metadataMode.SelectedIndex = (int)p.MetadataMode;
        clipboard.Checked = p.Destination.HasFlag(OutputDestination.Clipboard); file.Checked = p.Destination.HasFlag(OutputDestination.File); external.Checked = p.Destination.HasFlag(OutputDestination.ExternalApp); Error = null;
    }
    private CapturePreset ReadCurrent() => new()
    {
        Id = (list.SelectedItem as CapturePreset)?.Id ?? Guid.NewGuid().ToString("N"), IsDefault = (list.SelectedItem as CapturePreset)?.IsDefault == true, Name = presetName.Text.Trim(),
        SaveDirectory = Environment.ExpandEnvironmentVariables(directory.Text.Trim().Trim('"')), FileNamePattern = pattern.Text, ColorDepth = (ScreenshotColorDepth)color.SelectedIndex, OpenFolderAfterSave = openFolder.Checked, OcrSeparator = separator.Text,
        ImageEditorId = (imageApp.SelectedItem as ExternalApplication)?.Id, VideoEditorId = (videoApp.SelectedItem as ExternalApplication)?.Id, MetadataMode = (CaptureMetadataMode)metadataMode.SelectedIndex,
        Destination = (clipboard.Checked ? OutputDestination.Clipboard : 0) | (file.Checked ? OutputDestination.File : 0) | (external.Checked ? OutputDestination.ExternalApp : 0)
    };
    internal void StoreCurrent()
    {
        try
        {
            var p = ReadCurrent(); p.Validate();
            if (Presets.Any(x => x.Id != p.Id && string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("名前が重複しています。");
            ExternalApplication.ValidateReference(CurrentSettings().ImageEditors, p.ImageEditorId); ExternalApplication.ValidateReference(CurrentSettings().VideoEditors, p.VideoEditorId);
            if (list.SelectedIndex >= 0) { int index = list.SelectedIndex; list.Items[index] = p; list.SelectedIndex = index; }
            else { if (list.Items.Count == 0) p = p with { IsDefault = true }; list.Items.Add(p); list.SelectedIndex = list.Items.Count - 1; }
            Error = null; status.Text = "保存しました。設定画面の「保存」で確定します。"; status.ForeColor = Color.FromArgb(28, 37, 47);
        }
        catch (ArgumentException ex) { ShowError(ex.Message); }
    }
    private void ShowError(string text) { Error = text; status.Text = text; status.ForeColor = Color.DarkRed; }
    private static TableLayoutPanel Fields() { var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 7; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return t; }
    private static void Add(TableLayoutPanel table, string text, Control input, int row) { table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); input.Dock = DockStyle.Fill; input.Margin = new Padding(3, 3, 0, 3); table.Controls.Add(input, 1, row); }
    private static CheckBox Toggle(string text, string name) => new() { Text = text, Name = name, AutoSize = true };
    private static void RefreshApps(ComboBox combo, ExternalApplication[] apps, string? id)
    {
        combo.Items.Clear(); combo.Items.Add("指定しない"); combo.Items.AddRange(apps.Cast<object>().ToArray());
        if (!string.IsNullOrEmpty(id) && !apps.Any(a => a.Id == id)) combo.Items.Add(new ExternalApplication { Id = id, Name = "未登録: " + id });
        combo.SelectedIndex = Enumerable.Range(1, combo.Items.Count - 1).FirstOrDefault(i => ((ExternalApplication)combo.Items[i]!).Id == id, 0);
    }
}
