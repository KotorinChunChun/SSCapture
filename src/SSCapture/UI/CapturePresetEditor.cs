using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class CapturePresetEditor : UserControl
{
    private readonly ListBox list;
    private readonly TextBox presetName, directory, pattern, separator, whitePadding;
    private readonly CheckBox clipboard, file, external, pinned, openFolder;
    private readonly ComboBox imageApp, videoApp, metadataMode, color;
    private readonly NumericUpDown cropTop, cropBottom, cropLeft, cropRight;
    private readonly Label status;
    private readonly ComboBox copyContent = new() { Name = "ClipboardContent", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label temporaryPathWarning = new() { AutoSize = true, ForeColor = Color.DarkRed,
        Name = "TemporaryPathWarning", Text = "一時ファイルのパスは約1分後に使えなくなります。" };
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
    internal bool SelectPreset(string? id)
    {
        if (id is null) return false;
        if (list.SelectedItem is CapturePreset selected && selected.Id == id) return true;
        StoreCurrent();
        if (Error is not null) return false;
        int index = Array.FindIndex(Presets, p => p.Id == id);
        if (index < 0) return false;
        list.SelectedIndex = index;
        return true;
    }
    internal string? Error { get; private set; }
    internal CapturePresetEditor(AppSettings initial, Func<AppSettings> readSettings, Action<string>? openPresetShortcuts = null)
    {
        this.readSettings = readSettings; initialSettings = initial;
        Name = "CapturePresetEditor"; Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsEditorActions.PresetListWidth + SettingsEditorActions.PresetListGap)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsEditorActions.Height)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); Controls.Add(root);
        var listColumn = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = new Padding(0, 0, SettingsEditorActions.PresetListGap, 0) };
        listColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.Controls.Add(listColumn, 0, 0);
        list = new ListBox { Name = "CapturePresetList", Dock = DockStyle.Fill, Margin = Padding.Empty,
            IntegralHeight = false, FormattingEnabled = true, HorizontalScrollbar = true };
        list.Format += (_, e) => { if (e.ListItem is CapturePreset p) e.Value = (p.IsDefault ? "★ " : "") + p.Name; };
        listColumn.Controls.Add(list, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.Controls.Add(right, 1, 0);
        var header = Fields(); header.RowCount = 1; presetName = new TextBox { Name = "CapturePresetName", MaxLength = 80 }; Add(header, "名前", presetName, 0); right.Controls.Add(header, 0, 0);
        if (openPresetShortcuts is not null)
        {
            right.RowStyles[0].Height = 80;
            header.RowCount = 2;
            var open = SettingsForm.MakeButton("プリセット撮影を設定", "OpenOutputPresetShortcuts");
            open.Click += (_, _) =>
            {
                StoreCurrent();
                if (Error is null && editing is { } preset) openPresetShortcuts(preset.Id);
            };
            Add(header, "ショートカットキー", open, 1);
        }
        var tabs = new TabControl { Name = "OutputPresetSections", Dock = DockStyle.Fill }; right.Controls.Add(tabs, 0, 1);
        var output = Page("出力・画像"); naming = Page("保存・OCR"); var metadata = Page("撮影情報");
        var outputs = new FlowLayoutPanel { WrapContents = true, AutoSize = true, Dock = DockStyle.Fill };
        clipboard = Toggle("クリップボードへコピー", "ClipboardOutput"); file = Toggle("ファイル", "FileOutput"); external = Toggle("外部アプリ", "ExternalOutput");
        pinned = Toggle("ピン留め表示", "PinnedOutput"); outputs.Controls.AddRange([clipboard, file, external, pinned]); Add(output, "出力先", outputs, 0);
        output.RowStyles[0].Height = 70;
        copyContent.Items.AddRange(["画像", "テキスト（OCR）", "表（TSV）", "ファイルパス"]);
        output.RowCount = 9;
        output.RowStyles[7].SizeType = SizeType.Absolute;
        output.RowStyles[7].Height = 38;
        output.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Add(output, "静止画のコピー内容", copyContent, 1);
        output.Controls.Add(temporaryPathWarning, 1, 8);
        clipboard.CheckedChanged += (_, _) => UpdateCopyOptions();
        file.CheckedChanged += (_, _) => UpdateCopyOptions();
        copyContent.SelectedIndexChanged += (_, _) => UpdateCopyOptions();
        color = new ComboBox { Name = "ScreenshotColorDepth", DropDownStyle = ComboBoxStyle.DropDownList }; color.Items.AddRange(["原色を維持（減色なし）", "256色（8ビット）", "16色（4ビット）"]); Add(output, "静止画の減色", color, 2);
        imageApp = new ComboBox { Name = "PresetImageEditor", DropDownStyle = ComboBoxStyle.DropDownList }; Add(output, "画像の外部アプリ", imageApp, 3);
        videoApp = new ComboBox { Name = "PresetVideoEditor", DropDownStyle = ComboBoxStyle.DropDownList }; Add(output, "動画の外部アプリ", videoApp, 4);
        var cropFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        for (int i = 0; i < 4; i++) cropFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cropTop = AddCrop("上", nameof(WindowCrop.Top), 0);
        cropBottom = AddCrop("下", nameof(WindowCrop.Bottom), 1);
        cropLeft = AddCrop("左", nameof(WindowCrop.Left), 2);
        cropRight = AddCrop("右", nameof(WindowCrop.Right), 3);
        output.Controls.Add(new Label { Text = "選択したウィンドウの内側を切り抜く（px）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        output.SetColumnSpan(output.Controls[output.Controls.Count - 1], 2);
        output.Controls.Add(cropFields, 0, 6); output.SetColumnSpan(cropFields, 2);
        output.RowStyles[6].Height = 62;
        whitePadding = new TextBox { Name = "WhitePaddingPixels", MaxLength = 4, PlaceholderText = "空欄は0px（旧版で入力プリセットに設定した余白があればその値）" };
        Add(output, "白い余白（px）", whitePadding, 7);
        NumericUpDown AddCrop(string title, string edge, int column)
        {
            cropFields.Controls.Add(new Label { Text = title, Anchor = AnchorStyles.Left, AutoSize = true }, column, 0);
            var input = new NumericUpDown { Name = "WindowCrop" + edge, AccessibleName = title, Maximum = int.MaxValue, Dock = DockStyle.Fill };
            cropFields.Controls.Add(input, column, 1);
            return input;
        }
        directory = new TextBox { Name = "SaveDirectory" }; Add(naming, "保存フォルダ", directory, 0);
        pattern = new TextBox { Name = "FileNamePattern" }; Add(naming, "命名パターン", pattern, 1);
        separator = new TextBox { Name = "OcrSeparator" }; Add(naming, "OCR区切り", separator, 2);
        openFolder = Toggle("保存フォルダを開く", "OpenFolderAfterSave"); Add(naming, "保存後", openFolder, 3);
        var help = new Label { Text = "{yyyymmdd} 年月日 / {hhmmss} 時分秒 / {ext} png・mp4\n{ocr} OCR文字列 / {region} 範囲名 / {#}・{##} 連番", Dock = DockStyle.Fill }; naming.Controls.Add(help, 0, 6); naming.SetColumnSpan(help, 2); naming.SetRowSpan(help, 2);
        metadataMode = new ComboBox { Name = "MetadataMode", DropDownStyle = ComboBoxStyle.DropDownList }; metadataMode.Items.AddRange(["記録しない", "PNG内XMP", "別JSON", "PNG内XMPと別JSON"]); Add(metadata, "保存形式", metadataMode, 0);
        var warning = new Label { Dock = DockStyle.Fill, Text = "静止画をファイルに保存するときに、ポインタの位置、指していた画面部品の名前、アプリの実行ファイル名・ウィンドウ名を記録します。\n画像を共有する前に、ウィンドウ名やパスが含まれていないか確認してください。\nパスワード欄の名前、入力欄の値、画面全体の部品の一覧は記録しません。\n部品の情報は撮影と同じ瞬間のものとは限りません。取得できなかったときは理由を記録します。" }; metadata.Controls.Add(warning, 0, 1); metadata.SetColumnSpan(warning, 2); metadata.SetRowSpan(warning, 4);
        var add = SettingsForm.MakeButton("追加 ▼", "AddCapturePreset");
        var choices = new ContextMenuStrip { Name = "AddCapturePresetMenu" };
        add.ContextMenuStrip = choices;
        void Choice(string text, string id, Action action)
        {
            var item = new ToolStripMenuItem(text) { Name = id };
            item.Click += (_, _) => SaveNew(action);
            choices.Items.Add(item);
        }
        Choice("新規", "NewCapturePreset", () => Fill(CapturePreset.FromSettings(UniqueName("出力プリセット"), CurrentSettings()) with { WhitePaddingPixels = 0 }));
        Choice("直前の出力設定", "PreviousCapturePreset", () =>
        {
            if (PreviousOutput is not null) Fill(CapturePreset.FromSettings(UniqueName("直前の出力設定"), PreviousOutput) with { WhitePaddingPixels = PreviousOutput.ScreenshotPaddingPixels });
        });
        add.Click += (_, _) => { choices.Items[1].Enabled = PreviousOutput is not null; choices.Show(add, new Point(0, add.Height)); };
        Disposed += (_, _) => choices.Dispose();
        var duplicate = SettingsEditorActions.CreateButton("複製", "DuplicateCapturePreset", () => SaveNew(() =>
        {
            var source = ReadCurrent();
            Fill(source with { Id = Guid.NewGuid().ToString("N"), Name = UniqueName(source.Name + "のコピー"), IsDefault = false, BuiltInKey = null });
        }));
        var store = SettingsEditorActions.CreateButton("保存", "StoreCapturePreset", StoreCurrent, ButtonRole.Primary);
        var delete = SettingsEditorActions.CreateButton("削除", "DeleteCapturePreset", () =>
        {
            if (editing is not CapturePreset preset) return;
            if (preset.IsDefault) { ShowError("別のプリセットを既定にしてから削除してください。"); return; }
            ApplyCandidates(Presets.Where(candidate => candidate.Id != preset.Id).ToArray(), null);
        }, ButtonRole.Danger);
        var buttons = SettingsEditorActions.CreateRow("OutputPresetActions", add, duplicate, store, delete);
        root.Controls.Add(buttons, 0, 1);
        root.SetColumnSpan(buttons, 2);
        SettingsEditorActions.AddDefaultButton(listColumn, "DefaultCapturePreset", () =>
        {
            StoreCurrent();
            if (Error is not null || editing is not CapturePreset preset) return;
            ApplyCandidates(Presets.Select(candidate => candidate with { IsDefault = candidate.Id == preset.Id }).ToArray(), preset.Id);
        });
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
        file.CheckedChanged += (_, _) => UpdateFileFields(); pinned.CheckedChanged += (_, _) => UpdateFileFields(); UpdateFileFields();
        imageApp.Enter += (_, _) => RefreshApps(imageApp, CurrentSettings().ImageEditors, (imageApp.SelectedItem as ExternalApplication)?.Id);
        videoApp.Enter += (_, _) => RefreshApps(videoApp, CurrentSettings().VideoEditors, (videoApp.SelectedItem as ExternalApplication)?.Id);
        imageApp.SelectedIndexChanged += (_, _) => NotifyDefaultApplications();
        videoApp.SelectedIndexChanged += (_, _) => NotifyDefaultApplications();
        TableLayoutPanel Page(string title) { var page = new TabPage(title); var fields = Fields(); page.Controls.Add(fields); tabs.TabPages.Add(page); return fields; }
    }
    private void SaveNew(Action prepare)
    {
        try { prepare(); StoreCurrent(); }
        catch (ArgumentException ex) { ShowError(ex.Message); }
    }

    private AppSettings CurrentSettings() => IsHandleCreated ? readSettings() : initialSettings;
    internal FlowLayoutPanel AddSaveTools(Control ocrDirectory, Action<string> openDirectory)
    {
        Add(naming, "OCRエンジンの場所", ocrDirectory, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(3, 0, 0, 0), WrapContents = false };
        naming.RowStyles[5].Height = SettingsEditorActions.Height;
        naming.Controls.Add(actions, 1, 5);
        var folder = SettingsForm.MakeButton("保存先を開く", "OpenFolder");
        folder.Width = 144;
        folder.Margin = new Padding(0, 4, 8, 4);
        folder.CausesValidation = false;
        actions.Controls.Add(folder);

        string? FolderPath()
        {
            if (!file.Checked && !pinned.Checked) return null;
            string path = Environment.ExpandEnvironmentVariables(directory.Text.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? path : null;
        }
        void UpdateFolder() => folder.Enabled = FolderPath() is not null;
        file.CheckedChanged += (_, _) => UpdateFolder();
        pinned.CheckedChanged += (_, _) => UpdateFolder();
        directory.TextChanged += (_, _) => UpdateFolder();
        folder.Click += (_, _) => { if (FolderPath() is { } path) openDirectory(path); };
        UpdateFolder();
        return actions;
    }
    private void UpdateCopyOptions()
    {
        copyContent.Enabled = clipboard.Checked;
        temporaryPathWarning.Visible = clipboard.Checked && !file.Checked && copyContent.SelectedIndex == (int)StillClipboardContent.FilePath;
    }

    private void UpdateFileFields()
    {
        metadataMode.Enabled = openFolder.Enabled = file.Checked;
        directory.Enabled = file.Checked || pinned.Checked;
    }
    private void Fill(CapturePreset p)
    {
        editing = p;
        filling = true;
        try
        {
        RefreshApps(imageApp, CurrentSettings().ImageEditors, p.ImageEditorId); RefreshApps(videoApp, CurrentSettings().VideoEditors, p.VideoEditorId);
        whitePadding.Text = p.WhitePaddingPixels?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        copyContent.SelectedIndex = (int)p.ClipboardContent;
        cropTop.Value = p.WindowCrop.Top; cropBottom.Value = p.WindowCrop.Bottom;
        cropLeft.Value = p.WindowCrop.Left; cropRight.Value = p.WindowCrop.Right;
        presetName.Text = p.Name; directory.Text = p.SaveDirectory; pattern.Text = p.FileNamePattern; separator.Text = p.OcrSeparator; color.SelectedIndex = (int)p.ColorDepth; openFolder.Checked = p.OpenFolderAfterSave; metadataMode.SelectedIndex = (int)p.MetadataMode;
        clipboard.Checked = p.Destination.HasFlag(OutputDestination.Clipboard); file.Checked = p.Destination.HasFlag(OutputDestination.File); external.Checked = p.Destination.HasFlag(OutputDestination.ExternalApp); pinned.Checked = p.Destination.HasFlag(OutputDestination.Pinned); Error = null;
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
        Id = editing?.Id ?? Guid.NewGuid().ToString("N"), IsDefault = editing?.IsDefault == true, Name = presetName.Text.Trim(), BuiltInKey = editing?.BuiltInKey,
        SaveDirectory = Environment.ExpandEnvironmentVariables(directory.Text.Trim().Trim('"')), FileNamePattern = pattern.Text, ColorDepth = (ScreenshotColorDepth)color.SelectedIndex, OpenFolderAfterSave = openFolder.Checked, OcrSeparator = separator.Text,
        ImageEditorId = (imageApp.SelectedItem as ExternalApplication)?.Id, VideoEditorId = (videoApp.SelectedItem as ExternalApplication)?.Id, MetadataMode = (CaptureMetadataMode)metadataMode.SelectedIndex,
        ClipboardContent = (StillClipboardContent)copyContent.SelectedIndex,
        WhitePaddingPixels = ReadPadding(), PaddingPixels = editing?.PaddingPixels ?? 0,
        WindowCrop = new((int)cropTop.Value, (int)cropBottom.Value, (int)cropLeft.Value, (int)cropRight.Value),
        Destination = (clipboard.Checked ? OutputDestination.Clipboard : 0) | (file.Checked ? OutputDestination.File : 0) | (external.Checked ? OutputDestination.ExternalApp : 0) | (pinned.Checked ? OutputDestination.Pinned : 0)
    };
    private int? ReadPadding()
    {
        if (string.IsNullOrWhiteSpace(whitePadding.Text)) return null;
        if (int.TryParse(whitePadding.Text, out int pixels) && pixels is >= 0 and <= ScreenshotPadding.MaximumPixels)
            return pixels;
        throw new ArgumentException("白い余白は0～4096pxで指定してください。");
    }
    internal void StoreCurrent()
    {
        if (storing) return;
        storing = true;
        try
        {
            var p = ReadCurrent(); p.Validate(); FileNamePattern.Validate(p.FileNamePattern, p.OcrSeparator);
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
        LoadPresets(presets, selectedId);
    }
    internal void LoadPresets(CapturePreset[] presets, string? selectedId)
    {
        filling = true;
        try { list.Items.Clear(); list.Items.AddRange(presets); list.SelectedIndex = Math.Max(0, Array.FindIndex(presets, p => p.Id == selectedId)); }
        finally { filling = false; }
        if (list.SelectedItem is CapturePreset selected) Fill(selected);
        Error = null; status.Text = autoSave is null ? "編集内容は設定画面の「保存」で確定します。" : "自動保存しました。"; status.ForeColor = Color.FromArgb(28, 37, 47);
    }
    internal void EnableAutoSave(Func<CapturePreset[], string?> persist)
    {
        autoSave = persist; status.Text = "変更確定時に自動保存します。";
        PresetAutoSaveBinding.Attach(this, () => !filling && !storing, StoreCurrent);
    }
    internal void RefreshApplications()
    {
        var settings = CurrentSettings();
        RefreshApps(imageApp, settings.ImageEditors, (imageApp.SelectedItem as ExternalApplication)?.Id);
        RefreshApps(videoApp, settings.VideoEditors, (videoApp.SelectedItem as ExternalApplication)?.Id);
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
