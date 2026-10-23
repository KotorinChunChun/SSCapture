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
    private bool filling;
    private bool storing;
    private CapturePreset? editing;
    private Func<CapturePreset[], string?>? autoSave;
    internal AppSettings? PreviousOutput = null;
    internal event Action<string?, string?>? DefaultApplicationsChanged;
    internal void SetDefaultApplication(bool image, string id)
    {
        int index = Array.FindIndex(Presets, p => p.IsDefault);
        if (index < 0) return;
        var preset = (CapturePreset)list.Items[index]!;
        filling = true;
        try
        {
            list.Items[index] = image ? preset with { ImageEditorId = id } : preset with { VideoEditorId = id };
            if (list.SelectedIndex == index)
                RefreshApps(image ? imageApp : videoApp, image ? readSettings().ImageEditors : readSettings().VideoEditors, id);
        }
        finally { filling = false; }
    }
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
        clipboard = Toggle("クリップボードへコピー", "ClipboardOutput"); file = Toggle("ファイル", "FileOutput"); external = Toggle("外部アプリ", "ExternalOutput"); outputs.Controls.AddRange([clipboard, file, external]); Add(output, "出力先", outputs, 0);
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
        var buttons = new TableLayoutPanel { Name = "OutputPresetActions", Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        foreach (int _ in Enumerable.Range(0, 3)) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(buttons, 0, 1); root.SetColumnSpan(buttons, 2);
        var add = SettingsForm.MakeButton("追加 ▼", "AddCapturePreset"); add.Width = 144;
        var choices = new ContextMenuStrip { Name = "AddCapturePresetMenu" }; add.ContextMenuStrip = choices;
        void Choice(string text, string id, Action action)
        { var item = new ToolStripMenuItem(text) { Name = id }; item.Click += (_, _) => { try { action(); StoreCurrent(); } catch (ArgumentException ex) { ShowError(ex.Message); } }; choices.Items.Add(item); }
        Choice("新規", "NewCapturePreset", () => Fill(CapturePreset.FromSettings(UniqueName("出力プリセット"), CurrentSettings())));
        Choice("直前の出力設定", "PreviousCapturePreset", () => { if (PreviousOutput is not null) Fill(CapturePreset.FromSettings(UniqueName("直前の出力設定"), PreviousOutput)); });
        Choice("選択を複製", "DuplicateCapturePreset", () => { var source = ReadCurrent(); Fill(source with { Id = Guid.NewGuid().ToString("N"), Name = UniqueName(source.Name + "のコピー"), IsDefault = false }); });
        add.Click += (_, _) => { choices.Items[1].Enabled = PreviousOutput is not null; choices.Items[2].Enabled = editing is not null; choices.Show(add, new Point(0, add.Height)); }; add.Dock = DockStyle.Fill; add.Margin = new Padding(3); buttons.Controls.Add(add, 0, 0);
        Disposed += (_, _) => choices.Dispose();
        Button("削除", "DeleteCapturePreset", () => { if (editing is CapturePreset p) { if (p.IsDefault) { ShowError("別のプリセットを既定にしてから削除してください。"); return; } ApplyCandidates(Presets.Where(x => x.Id != p.Id).ToArray(), null); } });
        Button("既定にする", "DefaultCapturePreset", () => { StoreCurrent(); if (Error is not null || editing is not CapturePreset p) return; ApplyCandidates(Presets.Select(x => x with { IsDefault = x.Id == p.Id }).ToArray(), p.Id); });
        status = new Label { Name = "PresetEditorStatus", Dock = DockStyle.Fill, Text = "編集内容は設定画面の「保存」で確定します。" }; root.Controls.Add(status, 0, 2); root.SetColumnSpan(status, 2);
        list.SelectedIndexChanged += (_, _) =>
        {
            if (filling || list.SelectedItem is not CapturePreset p) return;
            if (autoSave is not null && editing is not null && p.Id != editing.Id)
            {
                StoreCurrent(); if (Error is not null) { filling = true; list.SelectedIndex = Array.FindIndex(Presets, x => x.Id == editing.Id); filling = false; return; }
                filling = true; list.SelectedIndex = Array.FindIndex(Presets, x => x.Id == p.Id); filling = false;
            }
            Fill(p);
        };
        foreach (var preset in initial.CapturePresets) list.Items.Add(preset);
        if (list.Items.Count > 0) list.SelectedIndex = Math.Max(0, Array.FindIndex(Presets, p => p.IsDefault)); else Fill(CapturePreset.FromSettings("", initial));
        file.CheckedChanged += (_, _) => UpdateFileFields(); UpdateFileFields();
        imageApp.Enter += (_, _) => RefreshApps(imageApp, CurrentSettings().ImageEditors, (imageApp.SelectedItem as ExternalApplication)?.Id);
        videoApp.Enter += (_, _) => RefreshApps(videoApp, CurrentSettings().VideoEditors, (videoApp.SelectedItem as ExternalApplication)?.Id);
        imageApp.SelectedIndexChanged += (_, _) => NotifyDefaultApplications();
        videoApp.SelectedIndexChanged += (_, _) => NotifyDefaultApplications();
        TableLayoutPanel Page(string title) { var page = new TabPage(title); var fields = Fields(); page.Controls.Add(fields); tabs.TabPages.Add(page); return fields; }
        void Button(string text, string name, Action action) { var b = SettingsForm.MakeButton(text, name); b.Dock = DockStyle.Fill; b.Margin = new Padding(3); b.Click += (_, _) => { try { action(); } catch (ArgumentException ex) { ShowError(ex.Message); } }; buttons.Controls.Add(b, buttons.Controls.Count, 0); }
    }
    private AppSettings CurrentSettings() => IsHandleCreated ? readSettings() : initialSettings;
    internal void AddSharedOcr(Control control) => Add(naming, "OCRエンジン（共通）", control, 4);
    private void UpdateFileFields() { metadataMode.Enabled = directory.Enabled = pattern.Enabled = separator.Enabled = openFolder.Enabled = file.Checked; }
    private void Fill(CapturePreset p)
    {
        editing = p;
        filling = true;
        try
        {
        RefreshApps(imageApp, CurrentSettings().ImageEditors, p.ImageEditorId); RefreshApps(videoApp, CurrentSettings().VideoEditors, p.VideoEditorId);
        presetName.Text = p.Name; directory.Text = p.SaveDirectory; pattern.Text = p.FileNamePattern; separator.Text = p.OcrSeparator; color.SelectedIndex = (int)p.ColorDepth; openFolder.Checked = p.OpenFolderAfterSave; metadataMode.SelectedIndex = (int)p.MetadataMode;
        clipboard.Checked = p.Destination.HasFlag(OutputDestination.Clipboard); file.Checked = p.Destination.HasFlag(OutputDestination.File); external.Checked = p.Destination.HasFlag(OutputDestination.ExternalApp); Error = null;
        }
        finally { filling = false; }
        NotifyDefaultApplications();
    }
    private void NotifyDefaultApplications()
    {
        if (!filling && editing is { IsDefault: true })
            DefaultApplicationsChanged?.Invoke((imageApp.SelectedItem as ExternalApplication)?.Id, (videoApp.SelectedItem as ExternalApplication)?.Id);
    }
    private CapturePreset ReadCurrent() => new()
    {
        Id = editing?.Id ?? Guid.NewGuid().ToString("N"), IsDefault = editing?.IsDefault == true, Name = presetName.Text.Trim(),
        SaveDirectory = Environment.ExpandEnvironmentVariables(directory.Text.Trim().Trim('"')), FileNamePattern = pattern.Text, ColorDepth = (ScreenshotColorDepth)color.SelectedIndex, OpenFolderAfterSave = openFolder.Checked, OcrSeparator = separator.Text,
        ImageEditorId = (imageApp.SelectedItem as ExternalApplication)?.Id, VideoEditorId = (videoApp.SelectedItem as ExternalApplication)?.Id, MetadataMode = (CaptureMetadataMode)metadataMode.SelectedIndex,
        Destination = (clipboard.Checked ? OutputDestination.Clipboard : 0) | (file.Checked ? OutputDestination.File : 0) | (external.Checked ? OutputDestination.ExternalApp : 0)
    };
    internal void StoreCurrent()
    {
        if (storing) return;
        storing = true;
        try
        {
            var p = ReadCurrent(); p.Validate();
            if (Presets.Any(x => x.Id != p.Id && string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("名前が重複しています。");
            ExternalApplication.ValidateReference(CurrentSettings().ImageEditors, p.ImageEditorId); ExternalApplication.ValidateReference(CurrentSettings().VideoEditors, p.VideoEditorId);
            var all = Presets.ToList(); int index = all.FindIndex(x => x.Id == p.Id);
            if (index >= 0) all[index] = p; else { if (all.Count == 0) p = p with { IsDefault = true }; all.Add(p); }
            ApplyCandidates(all.ToArray(), p.Id);
        }
        catch (ArgumentException ex) { ShowError(ex.Message); }
        finally { storing = false; }
    }
    private string UniqueName(string seed)
    { string result = seed; for (int suffix = 2; Presets.Any(p => p.Name.Equals(result, StringComparison.OrdinalIgnoreCase)); suffix++) result = seed + " " + suffix; return result; }
    private void ApplyCandidates(CapturePreset[] presets, string? selectedId)
    {
        string? error = autoSave?.Invoke(presets);
        if (error is not null) { ShowError(error); return; }
        filling = true;
        try { list.Items.Clear(); list.Items.AddRange(presets); list.SelectedIndex = Math.Max(0, Array.FindIndex(presets, p => p.Id == selectedId)); }
        finally { filling = false; }
        if (list.SelectedItem is CapturePreset selected) Fill(selected);
        Error = null; status.Text = autoSave is null ? "編集内容は設定画面の「保存」で確定します。" : "自動保存しました。"; status.ForeColor = Color.FromArgb(28, 37, 47);
    }
    internal void EnableAutoSave(Func<CapturePreset[], string?> persist)
    {
        autoSave = persist; status.Text = "変更確定時に自動保存します。";
        void Commit(object? sender, EventArgs e) { if (!filling && !storing) StoreCurrent(); }
        void Wire(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TextBox or NumericUpDown) { c.Validated += Commit; c.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Commit(c, EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; } }; }
                else if (c is CheckBox check) check.CheckedChanged += Commit;
                else if (c is ComboBox combo) combo.SelectedIndexChanged += Commit;
                Wire(c);
            }
        }
        Wire(this);
    }
    private void ShowError(string text) { Error = text; status.Text = text; status.ForeColor = Color.DarkRed; }
    private static TableLayoutPanel Fields() { var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 7; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return t; }
    private static void Add(TableLayoutPanel table, string text, Control input, int row) { table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); input.Dock = DockStyle.Fill; input.Margin = new Padding(3, 3, 0, 3); table.Controls.Add(input, 1, row); }
    private static CheckBox Toggle(string text, string name) => new() { Text = text, Name = name, AutoSize = true };
    private void RefreshApps(ComboBox combo, ExternalApplication[] apps, string? id)
    {
        bool prior = filling; filling = true;
        try
        {
        combo.Items.Clear(); combo.Items.Add("指定しない"); combo.Items.AddRange(apps.Cast<object>().ToArray());
        if (!string.IsNullOrEmpty(id) && !apps.Any(a => a.Id == id)) combo.Items.Add(new ExternalApplication { Id = id, Name = "未登録: " + id });
        combo.SelectedIndex = Enumerable.Range(1, combo.Items.Count - 1).FirstOrDefault(i => ((ExternalApplication)combo.Items[i]!).Id == id, 0);
        }
        finally { filling = prior; }
    }
}
