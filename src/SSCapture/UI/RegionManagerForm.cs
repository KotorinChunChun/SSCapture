using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class RegionManagerForm : Form
{
    private readonly List<SavedRegion> regions;
    private SavedRegion? lastRegion;
    private readonly Func<Size?, Task<Rectangle?>> selectRegion;
    private readonly Func<Size?, CaptureAnchor, Task<Rectangle?>>? selectAnchoredRegion;
    private readonly Func<SavedRegion[], string?> saveRegions;
    private readonly Func<IReadOnlyList<DisplayLayout>> displays;
    private readonly ListBox list;
    private readonly TextBox name;
    private readonly NumericUpDown x, y, width, height;
    private readonly HotkeyEditor shortcut;
    private readonly ComboBox anchor;
    private readonly Label displayInfo, status;
    private readonly Button delete, addLast, previewBounds;
    private readonly ComboBox captureMode;
    private readonly CheckBox coordinates, freezeFrame, includeCursor;
    private readonly NumericUpDown padding;
    private readonly AppSettings? profileSettings;
    private SavedRegion? editing;
    private Rectangle loadedBounds;
    private bool selecting, pickedBounds;
    private bool automaticName, updatingFields;
    private RegionPreviewForm? preview;
    private bool autoSave, reloading, committing;
    private CheckBox? systemAudio, micAudio;
    private ComboBox? microphone;
    private ToolStripMenuItem? addLastCommand;
    internal SavedRegion[] Regions => regions.Select(Clone).ToArray();
    internal bool SelectDefault()
    {
        if (editing?.IsDefault == true) return true;
        if (!StoreCurrent()) return false;
        var preset = regions.FirstOrDefault(p => p.IsDefault);
        if (preset is null) return false;
        ReloadList(preset.Id); return true;
    }
    private Form Host => TopLevel ? this : Parent?.FindForm() ?? this;

    public RegionManagerForm(SavedRegion[] regions, SavedRegion? lastRegion,
        Func<Size?, Task<Rectangle?>> selectRegion, Func<SavedRegion[], string?> saveRegions,
        Func<IReadOnlyList<DisplayLayout>>? displays = null,
        Func<Size?, CaptureAnchor, Task<Rectangle?>>? selectAnchoredRegion = null, bool embedded = false, AppSettings? profileSettings = null)
    {
        this.profileSettings = profileSettings;
        this.regions = regions.Select(Clone).ToList();
        this.lastRegion = lastRegion is null ? null : Clone(lastRegion);
        this.selectRegion = selectRegion;
        this.selectAnchoredRegion = selectAnchoredRegion;
        this.saveRegions = saveRegions;
        this.displays = displays ?? RegionCoordinates.ReadDisplays;
        Text = WindowTitle.Create("入力プリセット");
        Name = "RegionManagerForm";
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 12F);
        BackColor = Color.FromArgb(247, 249, 251);
        ForeColor = Color.FromArgb(28, 37, 47);
        ClientSize = new Size(1040, 604);
        MinimumSize = new Size(1020, 630);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();

        var root = new TableLayoutPanel { Name = "RegionManagerLayout", Dock = DockStyle.Fill,
            Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);
        var body = new TableLayoutPanel { Name = "RegionBody", Dock = DockStyle.Fill, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.Controls.Add(body, 0, 0);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = new Padding(0, 0, 12, 0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        body.Controls.Add(left, 0, 0);
        body.SetRowSpan(left, 2);
        list = new ListBox { Name = "SavedRegions", Dock = DockStyle.Fill, IntegralHeight = false,
            DisplayMember = nameof(SavedRegion.Name), Margin = Padding.Empty, FormattingEnabled = true, HorizontalScrollbar = true };
        list.Format += (_, e) =>
        {
            if (e.ListItem is SavedRegion region)
                e.Value = $"{(region.IsDefault ? "★ " : "")}{region.Name} / {(string.IsNullOrWhiteSpace(region.Shortcut) ? "なし" : region.Shortcut)} / {(region.UseFixedCoordinates ? $"({region.Bounds.X}, {region.Bounds.Y}) {region.Bounds.Width}×{region.Bounds.Height}" : SettingsForm.ModeName(region.Mode))}";
        };
        left.Controls.Add(list, 0, 0);
        var addNumeric = Button("新規（数値入力）", "NewNumericRegion", ButtonRole.Secondary);
        addNumeric.Click += (_, _) => NewRegion();
        left.Controls.Add(addNumeric, 0, 1);
        addLast = Button("前回の範囲から追加", "AddLastRegion", ButtonRole.Secondary);
        addLast.Enabled = lastRegion is not null;
        addLast.Click += (_, _) => AddLast();
        left.Controls.Add(addLast, 0, 2);

        var editor = new TableLayoutPanel { Name = "RegionEditor", Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 7, Margin = Padding.Empty };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float row in new[] { 44f, 44f, 88f, 40f, 48f, 48f, 0f })
            editor.RowStyles.Add(new RowStyle(row == 0 ? SizeType.Percent : SizeType.Absolute, row == 0 ? 100 : row));
        body.Controls.Add(editor, 1, 0);
        name = new TextBox { Name = "RegionName", Dock = DockStyle.Fill, MaxLength = 120 };
        AddField(editor, "名前", name, 0);
        shortcut = new HotkeyEditor("RegionShortcut", "") { Dock = DockStyle.Fill };
        AddField(editor, "再撮影ショートカット", shortcut, 1);
        var dimensions = new TableLayoutPanel { Name = "RegionDimensions", Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        dimensions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        dimensions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        x = Number("RegionX", int.MinValue, int.MaxValue);
        y = Number("RegionY", int.MinValue, int.MaxValue);
        width = Number("RegionWidth", 1, 32768);
        height = Number("RegionHeight", 1, 32768);
        Dimension("X", x, 0, 0); Dimension("Y", y, 2, 0);
        Dimension("幅", width, 0, 1); Dimension("高さ", height, 2, 1);
        AddField(editor, "範囲 (px)", dimensions, 2);
        anchor = new ComboBox { Name = "RegionAnchor", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        foreach (CaptureAnchor value in Enum.GetValues<CaptureAnchor>()) anchor.Items.Add(new AnchorItem(value));
        anchor.SelectedIndex = 0;
        AddField(editor, "基点", anchor, 3);
        var pickers = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        pickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var draw = Button("囲み直す", "DrawRegion", ButtonRole.Secondary);
        draw.Click += async (_, _) => await SelectBoundsAsync(null);
        var fixedSize = Button("この寸法で位置を選ぶ", "PickFixedRegion", ButtonRole.Secondary);
        fixedSize.Click += async (_, _) => await SelectBoundsAsync(new Size((int)width.Value, (int)height.Value));
        pickers.Controls.Add(draw, 0, 0); pickers.Controls.Add(fixedSize, 1, 0);
        editor.Controls.Add(pickers, 0, 4); editor.SetColumnSpan(pickers, 2);
        var check = previewBounds = Button("範囲を確認", "PreviewRegion", ButtonRole.Secondary);
        check.Click += (_, _) => PreviewRegion();
        editor.Controls.Add(check, 0, 5); editor.SetColumnSpan(check, 2);
        displayInfo = new Label { Name = "RegionDisplayInfo", Dock = DockStyle.Fill, AutoEllipsis = true,
            Padding = new Padding(0, 4, 0, 0) };
        editor.Controls.Add(displayInfo, 0, 6); editor.SetColumnSpan(displayInfo, 2);
        var actions = new TableLayoutPanel { Name = "RegionActions", Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(actions, 1, 1);
        var saveSelected = Button("この範囲を保存", "SaveRegion", ButtonRole.Primary);
        saveSelected.Click += (_, _) => SaveRegion();
        actions.Controls.Add(saveSelected, 0, 0);
        delete = Button("選択範囲を削除", "DeleteRegion", ButtonRole.Danger);
        delete.Click += (_, _) => DeleteRegion();
        actions.Controls.Add(delete, 1, 0);
        status = new Label { Name = "RegionStatus", Dock = DockStyle.Fill, AutoEllipsis = true,
            Padding = new Padding(0, 6, 0, 0) };
        root.Controls.Add(status, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = Padding.Empty };
        var close = Button("閉じる", "CloseRegions", ButtonRole.Neutral);
        close.Dock = DockStyle.None; close.Size = new Size(120, 36); close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        root.Controls.Add(footer, 0, 2);
        CancelButton = close;
        // 名前・キーを共通ヘッダーへ移し、範囲編集と取得条件を分けてスクロールを不要にする。
        body.Controls.Remove(editor);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.Controls.Add(right, 1, 0);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (var control in new Control[] { name, shortcut })
        {
            int row = editor.GetRow(control); var label = editor.GetControlFromPosition(0, row)!;
            editor.Controls.Remove(label); editor.Controls.Remove(control); header.Controls.Add(label, 0, row); header.Controls.Add(control, 1, row); editor.RowStyles[row].Height = 0;
        }
        right.Controls.Add(header, 0, 0);
        var sections = new TabControl { Name = "InputPresetSections", Dock = DockStyle.Fill }; right.Controls.Add(sections, 0, 1);
        var conditionPage = new TabPage("取得条件"); var boundsPage = new TabPage("座標・寸法"); sections.TabPages.AddRange([conditionPage, boundsPage]); boundsPage.Controls.Add(editor);
        var conditions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        conditions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184)); conditions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 6; row++) conditions.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 5 ? 52 : 38)); conditions.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); conditionPage.Controls.Add(conditions);
        captureMode = new ComboBox { Name = "CaptureMode", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var mode in Enum.GetValues<CaptureMode>()) captureMode.Items.Add(SettingsForm.ModeName(mode)); captureMode.SelectedIndex = 0; AddField(conditions, "撮影モード", captureMode, 0);
        if (profileSettings is not null) captureMode.Items.Add("固定位置とサイズ");
        coordinates = new CheckBox { Name = "InputFixedCoordinates", Text = "登録した座標を使用する", AutoSize = true, Dock = DockStyle.Fill }; AddField(conditions, "位置", coordinates, 1);
        freezeFrame = new CheckBox { Name = "FreezeFrame", Text = "撮影開始時の画面を固定", AutoSize = true, Dock = DockStyle.Fill }; AddField(conditions, "撮影タイミング", freezeFrame, 2);
        includeCursor = new CheckBox { Name = "IncludeCursor", Text = "画像・動画に含める", AutoSize = true, Dock = DockStyle.Fill }; AddField(conditions, "カーソル", includeCursor, 3);
        padding = Number("ScreenshotPaddingPixels", 0, ScreenshotPadding.MaximumPixels); AddField(conditions, "画像の余白 (px)", padding, 4);
        var profileButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; conditions.Controls.Add(profileButtons, 0, 5); conditions.SetColumnSpan(profileButtons, 2);
        var duplicate = Button("複製", "DuplicateInputPreset", ButtonRole.Secondary); duplicate.Width = 120; duplicate.Dock = DockStyle.None;
        duplicate.Click += (_, _) => { string copiedName = name.Text; list.ClearSelected(); editing = null; automaticName = false; name.Text = copiedName + "のコピー"; }; profileButtons.Controls.Add(duplicate);
        var makeDefault = Button("既定にする", "DefaultInputPreset", ButtonRole.Secondary); makeDefault.Width = 144; makeDefault.Dock = DockStyle.None;
        foreach (var button in new[] { duplicate, makeDefault })
        { button.AutoSize = true; button.MinimumSize = new Size(button.Width, 36); button.Height = Math.Max(36, button.GetPreferredSize(Size.Empty).Height); }
        makeDefault.Click += (_, _) => MakeDefault(); profileButtons.Controls.Add(makeDefault);
        void UpdateCoordinates() { x.Enabled = y.Enabled = previewBounds.Enabled = coordinates.Checked; captureMode.Enabled = profileSettings is not null || !coordinates.Checked; }
        coordinates.CheckedChanged += (_, _) => UpdateCoordinates();
        list.SelectedIndexChanged += (_, _) =>
        {
            if (reloading || list.SelectedItem is not SavedRegion region) return;
            if (autoSave && editing is not null && region.Id != editing.Id)
            {
                string previous = editing.Id;
                if (!StoreCurrent()) { ReloadList(previous, load: false); return; }
                ReloadList(region.Id); return;
            }
            LoadRegion(region);
        };
        name.TextChanged += (_, _) => { if (!updatingFields) automaticName = false; };
        shortcut.ValueChanged += (_, _) => UpdateAutomaticName();
        foreach (NumericUpDown number in new[] { x, y, width, height }) number.ValueChanged += (_, _) => UpdateAutomaticName();
        FormClosing += (_, e) => { if (selecting) e.Cancel = true; };
        ReloadList(this.regions.FirstOrDefault(p => p.IsDefault)?.Id);
        if (regions.Length == 0) NewRegion();
        if (embedded)
        {
            TopLevel = false; FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; Dock = DockStyle.Fill;
            AutoScroll = false; root.Dock = DockStyle.Fill; root.Padding = new Padding(4);
            root.RowStyles[2].Height = 0; footer.Visible = false;
            editor.ColumnStyles[0].Width = 184;
            body.ColumnStyles[0].Width = 25; body.ColumnStyles[1].Width = 75;
            CancelButton = null;
        }
        UpdateCoordinates();
        if (profileSettings is null)
        {
            body.Controls.Remove(right); boundsPage.Controls.Remove(editor); body.Controls.Add(editor, 1, 0);
            foreach (var control in new Control[] { name, shortcut })
            {
                int row = header.GetRow(control); var label = header.GetControlFromPosition(0, row)!;
                header.Controls.Remove(label); header.Controls.Remove(control); editor.Controls.Add(label, 0, row); editor.Controls.Add(control, 1, row); editor.RowStyles[row].Height = 44;
            }
            Disposed += (_, _) => right.Dispose();
        }
        else
        {
            width.Name = "FixedWidth"; height.Name = "FixedHeight"; anchor.Name = "FixedCaptureAnchor";
            conditions.Controls.Remove(conditions.GetControlFromPosition(0, 1)!); conditions.Controls.Remove(coordinates); conditions.RowStyles[1].Height = 0;
            captureMode.SelectedIndexChanged += (_, _) => { if (!updatingFields) coordinates.Checked = captureMode.SelectedIndex == 5; };
            if (editing?.UseFixedCoordinates == true) captureMode.SelectedIndex = 5;
            left.Controls.Remove(addNumeric); left.Controls.Remove(addLast); left.RowStyles[1].Height = left.RowStyles[2].Height = 0;
            conditions.Controls.Remove(profileButtons); profileButtons.Controls.Remove(makeDefault);
            actions.Controls.Clear(); body.Controls.Remove(actions); body.Controls.Add(actions, 0, 1); body.SetColumnSpan(actions, 2);
            body.SetRowSpan(left, 1); body.RowStyles[1].Height = 44;
            actions.ColumnCount = 3; actions.ColumnStyles.Clear();
            foreach (int i in Enumerable.Range(0, 3)) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            var add = Button("追加 ▼", "AddInputPreset", ButtonRole.Secondary);
            var choices = new ContextMenuStrip { Name = "AddInputPresetMenu" };
            add.ContextMenuStrip = choices;
            void Choice(string text, string id, Action action)
            {
                var item = new ToolStripMenuItem(text) { Name = id };
                item.Click += (_, _) => { bool previous = updatingFields; updatingFields = true; try { action(); } finally { updatingFields = previous; } StoreCurrent(); };
                choices.Items.Add(item);
            }
            Choice("新規", "NewInputPreset", NewRegion);
            Choice("前回の撮影範囲", "AddLastRegion", AddLast); addLastCommand = (ToolStripMenuItem)choices.Items[1]; addLastCommand.Enabled = lastRegion is not null;
            Choice("選択を複製", "DuplicateInputPreset", () =>
            {
                var source = ReadRegion(); string seed = source.Name + "のコピー", nextName = seed;
                for (int suffix = 2; this.regions.Any(r => r.Name == nextName); suffix++) nextName = seed + " " + suffix;
                LoadRegion(source with { Id = Guid.NewGuid().ToString("N"), Name = nextName, Shortcut = "", IsDefault = false });
            });
            add.Click += (_, _) => { choices.Items[2].Enabled = editing is not null; choices.Show(add, new Point(0, add.Height)); };
            Disposed += (_, _) => choices.Dispose();
            delete.Text = "削除"; makeDefault.Dock = DockStyle.Fill; makeDefault.AutoSize = false; makeDefault.MinimumSize = Size.Empty;
            actions.Controls.Add(add, 0, 0); actions.Controls.Add(delete, 1, 0); actions.Controls.Add(makeDefault, 2, 0);
        }

        void Dimension(string label, Control control, int column, int row)
        {
            dimensions.Controls.Add(Label(label), column, row);
            dimensions.Controls.Add(control, column + 1, row);
        }
    }

    private static SavedRegion Clone(SavedRegion region) => region with { Layout = region.Layout.ToArray() };
    internal void AttachRecordingSettings(CheckBox system, CheckBox mic, ComboBox device)
    {
        systemAudio = system; micAudio = mic; microphone = device;
        var sections = (TabControl)Controls.Find("InputPresetSections", true).Single();
        var page = new TabPage("録画・音声");
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int _ in Enumerable.Range(0, 3)) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var (control, label, row) in new[] { ((Control)system, "PCの再生音", 0), (mic, "マイク", 1), (device, "使用するマイク", 2) })
        { control.Parent?.Controls.Remove(control); AddField(fields, label, control, row); }
        page.Controls.Add(fields); sections.TabPages.Add(page);
        if (editing is not null) LoadAudio(editing);
    }
    private void LoadAudio(SavedRegion region)
    {
        if (systemAudio is null || micAudio is null || microphone is null) return;
        systemAudio.Checked = region.RecordSystemAudio; micAudio.Checked = region.RecordMicrophone;
        int index = Enumerable.Range(0, microphone.Items.Count).FirstOrDefault(i => ((Recording.MicrophoneInfo)microphone.Items[i]!).Id == region.MicrophoneDeviceId, -1);
        if (index < 0) { microphone.Items.Add(new Recording.MicrophoneInfo(region.MicrophoneDeviceId, "未接続のマイク")); index = microphone.Items.Count - 1; }
        microphone.SelectedIndex = index;
    }
    internal void EnableAutoSave()
    {
        autoSave = true;
        PresetAutoSaveBinding.Attach(this, () => !updatingFields && !reloading && !committing && !selecting,
            () => StoreCurrent());
    }
    private void MakeDefault()
    {
        if (!StoreCurrent() || editing is null) return;
        var candidate = regions.Select(r => r with { IsDefault = r.Id == editing.Id }).ToArray();
        string? error = saveRegions(candidate);
        if (error is not null) { SetStatus(error, true); return; }
        string id = editing.Id; regions.Clear(); regions.AddRange(candidate); ReloadList(id);
    }
    internal void UpdateLastRegion(SavedRegion? region)
    {
        lastRegion = region is null ? null : Clone(region);
        addLast.Enabled = lastRegion is not null;
        if (addLastCommand is not null) addLastCommand.Enabled = lastRegion is not null;
    }

    private static Button Button(string text, string name, ButtonRole role)
    {
        var button = new Button { Text = text, Name = name, Dock = DockStyle.Fill, Margin = new Padding(3) };
        ButtonTheme.Apply(button, role);
        return button;
    }
    private static Label Label(string text) => new()
    { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 3, 0) };
    private static NumericUpDown Number(string name, decimal minimum, decimal maximum) => new()
    { Name = name, Minimum = minimum, Maximum = maximum, DecimalPlaces = 0, Dock = DockStyle.Fill, ThousandsSeparator = false };
    private static void AddField(TableLayoutPanel table, string label, Control control, int row)
    { table.Controls.Add(Label(label), 0, row); table.Controls.Add(control, 1, row); }

    private void NewRegion()
    {
        bool previousUpdate = updatingFields;
        list.ClearSelected(); editing = null; pickedBounds = false; delete.Enabled = false;
        updatingFields = true;
        automaticName = true;
        try
        {
            name.Text = ""; shortcut.Value = "";
            captureMode.SelectedIndex = (int)(profileSettings?.DefaultMode ?? CaptureMode.FixedRegion);
            coordinates.Checked = profileSettings is null; freezeFrame.Checked = profileSettings?.FreezeFrame ?? true; includeCursor.Checked = profileSettings?.IncludeCursor ?? false; padding.Value = profileSettings?.ScreenshotPaddingPixels ?? 0;
            if (systemAudio is not null && profileSettings is not null) LoadAudio(SavedRegion.FromSettings("新規", profileSettings));
            var connected = displays();
            Rectangle screen = connected.Count > 0 ? connected[0].Bounds : new Rectangle(0, 0, 640, 480);
            SetBoundsFields(new Rectangle(screen.Location, new Size(Math.Min(640, screen.Width), Math.Min(480, screen.Height))));
            ShowDisplays(connected);
            name.Text = RegionAutoName.Create(shortcut.Value, ReadBounds());
            SetStatus("新規登録（未保存）");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            updatingFields = previousUpdate; UpdateAutomaticName();
            if (profileSettings is not null)
            {
                string seed = name.Text, nextName = seed;
                for (int suffix = 2; regions.Any(r => r.Name.Equals(nextName, StringComparison.OrdinalIgnoreCase)); suffix++) nextName = seed + " " + suffix;
                if (nextName != seed) { automaticName = false; name.Text = nextName; }
            }
        }
    }

    private void AddLast()
    {
        if (lastRegion is null) return;
        try
        {
            var connected = displays();
            Rectangle bounds = RegionCoordinates.Resolve(lastRegion, connected);
            NewRegion(); SetBoundsFields(bounds); pickedBounds = true; coordinates.Checked = true;
            if (profileSettings is not null) captureMode.SelectedIndex = 5;
            ShowDisplays(connected); SetStatus("前回の範囲を読み込みました（未保存）。");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void LoadRegion(SavedRegion region)
    {
        editing = region; pickedBounds = false; delete.Enabled = true;
        updatingFields = true;
        automaticName = region.Name == RegionAutoName.Create(region.Shortcut, region.Bounds);
        name.Text = region.Name; shortcut.Value = region.Shortcut;
        captureMode.SelectedIndex = profileSettings is not null && region.UseFixedCoordinates ? 5 : (int)region.Mode; coordinates.Checked = region.UseFixedCoordinates; freezeFrame.Checked = region.FreezeFrame; includeCursor.Checked = region.IncludeCursor; padding.Value = region.PaddingPixels; anchor.SelectedIndex = (int)region.FixedAnchor;
        LoadAudio(region);
        try
        {
            var connected = displays();
            SetBoundsFields(region.UseFixedCoordinates ? RegionCoordinates.Resolve(region, connected) : region.Bounds);
            ShowDisplays(connected); SetStatus("");
        }
        catch (Exception ex)
        {
            SetBoundsFields(region.Bounds);
            displayInfo.Text = "登録時: " + string.Join(" / ", region.Layout.Select(d => $"{d.Name} ({d.Bounds.Width}×{d.Bounds.Height} px)"));
            SetStatus(ex.Message + " 囲み直すか座標を変更して保存すると、現在の画面構成で再登録します。", true);
        }
        finally { updatingFields = false; UpdateAutomaticName(); }
    }

    private void SetBoundsFields(Rectangle bounds)
    {
        bool previousUpdate = updatingFields;
        updatingFields = true;
        x.Value = bounds.X; y.Value = bounds.Y;
        width.Value = Math.Clamp(bounds.Width, (int)width.Minimum, (int)width.Maximum);
        height.Value = Math.Clamp(bounds.Height, (int)height.Minimum, (int)height.Maximum);
        loadedBounds = bounds;
        updatingFields = previousUpdate;
        UpdateAutomaticName();
    }

    private void UpdateAutomaticName()
    {
        if (updatingFields || !automaticName) return;
        updatingFields = true;
        try { name.Text = RegionAutoName.Create(shortcut.Value, ReadBounds()); }
        finally { updatingFields = false; }
    }

    private Rectangle ReadBounds() => new((int)x.Value, (int)y.Value, (int)width.Value, (int)height.Value);
    private SavedRegion ReadRegion()
    {
        Rectangle bounds = ReadBounds();
        SavedRegion next = !coordinates.Checked ? (editing ?? new SavedRegion()) with { Bounds = bounds }
            : editing is not null && editing.UseFixedCoordinates && !pickedBounds && bounds == loadedBounds
            ? editing with { Name = name.Text.Trim(), Shortcut = shortcut.Value }
            : RegionCoordinates.FromBounds(name.Text.Trim(), bounds, displays(), editing?.Id, shortcut.Value);
        next = next with { Name = name.Text.Trim(), Shortcut = shortcut.Value, ProfileVersion = 2, UseFixedCoordinates = coordinates.Checked,
            Mode = captureMode.SelectedIndex == 5 ? CaptureMode.FixedRegion : (CaptureMode)captureMode.SelectedIndex, FixedAnchor = ((AnchorItem)anchor.SelectedItem!).Value,
            FreezeFrame = freezeFrame.Checked, IncludeCursor = includeCursor.Checked, PaddingPixels = (int)padding.Value,
            IsDefault = editing?.IsDefault == true || (profileSettings is not null && regions.Count == 0),
            RecordSystemAudio = systemAudio?.Checked ?? editing?.RecordSystemAudio ?? profileSettings?.RecordSystemAudio ?? false,
            RecordMicrophone = micAudio?.Checked ?? editing?.RecordMicrophone ?? profileSettings?.RecordMicrophone ?? false,
            MicrophoneDeviceId = (microphone?.SelectedItem as Recording.MicrophoneInfo)?.Id ?? editing?.MicrophoneDeviceId ?? profileSettings?.MicrophoneDeviceId ?? "" };
        next.Validate();
        return next;
    }

    private void SaveRegion() => StoreCurrent();
    internal bool StoreCurrent()
    {
        if (selecting || committing) return false;
        committing = true;
        try
        {
            SavedRegion next = ReadRegion();
            if ((editing is null || next.Name != editing.Name) && regions.Any(r => r.Id != next.Id && string.Equals(r.Name, next.Name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("名前が重複しています。");
            var candidate = regions.Select(Clone).ToList();
            int index = candidate.FindIndex(region => region.Id == next.Id);
            if (index < 0) candidate.Add(next); else candidate[index] = next;
            string? error = saveRegions(candidate.Select(Clone).ToArray());
            if (error is not null) { SetStatus(error, true); return false; }
            regions.Clear(); regions.AddRange(candidate); ReloadList(next.Id);
            SetStatus("入力プリセットを保存しました。");
            return true;
        }
        catch (Exception ex) { SetStatus("保存できませんでした: " + ex.Message, true); return false; }
        finally { committing = false; }
    }

    private void DeleteRegion()
    {
        if (selecting || editing is null) return;
        if (editing.IsDefault) { SetStatus("別の入力プリセットを既定にしてから削除してください。", true); return; }
        try
        {
            var candidate = regions.Where(region => region.Id != editing.Id).Select(Clone).ToArray();
            string? error = saveRegions(candidate.Select(Clone).ToArray());
            if (error is not null) { SetStatus(error, true); return; }
            regions.Clear(); regions.AddRange(candidate); ReloadList(null);
            if (candidate.Length == 0) NewRegion();
            SetStatus("選択した入力プリセットを削除しました。");
        }
        catch (Exception ex) { SetStatus("削除できませんでした: " + ex.Message, true); }
    }

    private void ReloadList(string? selectedId, bool load = true)
    {
        reloading = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear(); list.Items.AddRange(regions.Cast<object>().ToArray());
            list.SelectedIndex = regions.Count == 0 ? -1 : Math.Max(0, regions.FindIndex(region => region.Id == selectedId));
        }
        finally { list.EndUpdate(); reloading = false; }
        if (load && list.SelectedItem is SavedRegion region) LoadRegion(region);
    }

    private async Task SelectBoundsAsync(Size? fixedSize)
    {
        if (selecting) return;
        Form host = Host;
        bool restore = host.Visible;
        selecting = true; Enabled = false;
        try
        {
            if (restore) CaptureUiPreparation.Prepare([host], () => { });
            CaptureAnchor selectedAnchor = ((AnchorItem)anchor.SelectedItem!).Value;
            Rectangle? bounds = await (selectAnchoredRegion is null ? selectRegion(fixedSize) : selectAnchoredRegion(fixedSize, selectedAnchor));
            if (IsDisposed) return;
            if (bounds is null) { SetStatus("範囲選択をキャンセルしました。"); return; }
            if (fixedSize is { } expected && bounds.Value.Size != expected)
                throw new InvalidOperationException("選択した範囲が指定寸法と一致しません。もう一度位置を選んでください。");
            var connected = displays();
            _ = RegionCoordinates.FromBounds("選択範囲", bounds.Value, connected);
            SetBoundsFields(bounds.Value); pickedBounds = true; coordinates.Checked = true; ShowDisplays(connected);
            if (profileSettings is not null) captureMode.SelectedIndex = 5;
            SetStatus("選択した範囲は未保存です。");
        }
        catch (Exception ex) { if (!IsDisposed) SetStatus("範囲を選択できませんでした: " + ex.Message, true); }
        finally
        {
            selecting = false;
            if (autoSave && !IsDisposed) StoreCurrent();
            if (!IsDisposed) { Enabled = true; if (restore && !host.IsDisposed) WindowVisibility.Show(host); }
        }
    }

    private void PreviewRegion()
    {
        if (selecting) return;
        if (!coordinates.Checked) { SetStatus("位置が未指定です。撮影モードを「固定位置とサイズ」にするか、先に範囲を選んでください。"); return; }
        try
        {
            Rectangle bounds = RegionCoordinates.Resolve(ReadRegion(), displays());
            preview?.Close();
            Form host = Host;
            bool restore = host.Visible;
            preview = new RegionPreviewForm(bounds);
            preview.FormClosed += (_, _) => { preview = null; if (restore && !host.IsDisposed) WindowVisibility.Show(host); };
            if (restore) host.Hide();
            WindowVisibility.Show(preview);
        }
        catch (Exception ex) { SetStatus("範囲を表示できませんでした: " + ex.Message, true); }
    }

    private void ShowDisplays(IReadOnlyList<DisplayLayout> connected) => displayInfo.Text =
        "接続中: " + string.Join(" / ", connected.Select(display => $"{display.Name} ({display.Bounds.Width}×{display.Bounds.Height} px)"));
    private void SetStatus(string text, bool error = false)
    { status.Text = text; status.ForeColor = error ? Color.FromArgb(166, 35, 35) : Color.FromArgb(50, 74, 82); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { RegionPreviewForm? old = preview; preview = null; old?.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record AnchorItem(CaptureAnchor Value)
    {
        public override string ToString() => Value switch
        {
            CaptureAnchor.TopLeft => "左上", CaptureAnchor.TopRight => "右上",
            CaptureAnchor.BottomRight => "右下", CaptureAnchor.BottomLeft => "左下", _ => "左上"
        };
    }
}
