using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class ExternalApplicationsEditor : UserControl
{
    private readonly List<ExternalApplication> applications;
    private readonly ComboBox selected;
    private const int VisibleRows = 6;
    private readonly DataGridView list;
    private readonly TextBox appName;
    internal TextBox Executable { get; }
    internal TextBox Arguments { get; }
    private readonly Label status = null!;
    private readonly CaptureIntent intent;
    private readonly Func<(IReadOnlyList<CapturePreset> Presets, string NotificationId)> readUsage;
    private readonly ContextMenuStrip newMenu = new();
    private readonly ToolTip argumentTips = new() { AutoPopDelay = 25000, ShowAlways = true };
    internal ExternalApplication[] Applications => applications.ToArray();
    internal string SelectedId => (selected.SelectedItem as ExternalApplication)?.Id ?? "";
    internal ExternalApplication? SelectedApplication => selected.SelectedItem as ExternalApplication;
    private ExternalApplication? EditingApplication => list.SelectedRows.Count == 1
        ? list.SelectedRows[0].Tag as ExternalApplication : null;
    private bool refreshing;
    private ExternalApplicationLaunch launch;
    internal event Action<string>? SelectedChanged;
    internal void SelectApplication(string id)
    {
        refreshing = true;
        try { selected.SelectedIndex = applications.FindIndex(a => a.Id == id) + 1; }
        finally { refreshing = false; }
    }

    internal ExternalApplicationsEditor(CaptureIntent intent, ExternalApplication[] apps, string selectedId, string legacyPath,
        string legacyArgs, IReadOnlyList<EditorPreset> examples, Func<(IReadOnlyList<CapturePreset> Presets, string NotificationId)> readUsage)
    {
        string prefix = intent == CaptureIntent.Screenshot ? "Image" : "Video";
        Name = prefix + "ApplicationsEditor"; Dock = DockStyle.Fill;
        this.intent = intent;
        this.readUsage = readUsage;
        applications = apps.ToList();
        if (apps.Length == 0 && !string.IsNullOrWhiteSpace(legacyPath))
        {
            var legacy = new ExternalApplication { Name = Path.GetFileNameWithoutExtension(legacyPath), Executable = legacyPath, Arguments = legacyArgs };
            applications.Add(legacy); selectedId = legacy.Id;
        }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 5; row++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);
        selected = new ComboBox { Name = prefix + "SelectedEditor", DropDownStyle = ComboBoxStyle.DropDownList };
        Add("既定出力のアプリ", selected, 0);
        selected.SelectedIndexChanged += (_, _) =>
        {
            if (refreshing) return;
            SelectedChanged?.Invoke(SelectedId);
            RefreshUsage();
        };
        list = new DataGridView
        {
            Name = prefix + "EditorList", Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ScrollBars = ScrollBars.Vertical,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            BackgroundColor = SystemColors.Window
        };
        list.Columns.AddRange(Column("ApplicationName", "アプリ名", 25), Column("Notification", "通知", 18),
            Column("OutputPresets", "出力プリセット", 57));
        root.Controls.Add(list, 0, 1); root.SetColumnSpan(list, 2);
        UpdateListHeight();
        FontChanged += (_, _) => UpdateListHeight();
        DpiChangedAfterParent += (_, _) => UpdateListHeight();
        VisibleChanged += (_, _) => { if (Visible) RefreshUsage(); };
        appName = new TextBox { Name = prefix + "EditorName", MaxLength = 80 }; Add("登録名", appName, 2);
        Executable = new TextBox { Name = prefix + "EditorPath", Text = legacyPath }; Add("実行ファイル", Executable, 3);
        Arguments = new TextBox { Name = prefix + "EditorArguments", Text = legacyArgs }; Add("引数", Arguments, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        root.Controls.Add(buttons, 0, 5); root.SetColumnSpan(buttons, 2);
        var newButton = SettingsForm.MakeButton("新規 ▾", prefix + "EditorNew"); newButton.Width = 92;
        newButton.ContextMenuStrip = newMenu;
        foreach (var preset in examples)
        {
            var item = new ToolStripMenuItem(preset.Name) { Name = preset.Name };
            item.Click += (_, _) =>
            {
                list.ClearSelection();
                SetLaunch(preset.Launch, preset.Executable, preset.Arguments);
                appName.Text = preset.Launch != ExternalApplicationLaunch.Executable ? preset.Name
                    : string.IsNullOrWhiteSpace(preset.Executable) ? "" : Path.GetFileNameWithoutExtension(preset.Executable);
                status.Text = "新規入力です。「保存」で一覧に登録し、設定の「保存」で確定します。";
            };
            newMenu.Items.Add(item);
        }
        newMenu.Opening += (_, _) =>
        {
            for (int i = 0; i < examples.Count; i++)
            {
                var preset = examples[i];
                if (preset.Launch == ExternalApplicationLaunch.Executable) continue;
                bool available = PngEditorAssociation.Find(preset.Launch) is not null;
                newMenu.Items[i].Enabled = available;
                newMenu.Items[i].ToolTipText = available ? "" : "このPCにPNGを開く関連付けがありません。";
            }
        };
        newButton.Click += (_, _) => newMenu.Show(newButton, 0, newButton.Height);
        buttons.Controls.Add(newButton);
        Button("複製", "Duplicate", () => { list.ClearSelection(); appName.Text += " コピー"; });
        Button("保存", "Store", Store);
        Button("削除", "Delete", Delete);
        status = new Label { Name = prefix + "EditorStatus", Dock = DockStyle.Fill, Text = "「保存」で一覧登録、設定の「保存」で確定。引数欄のツールチップで置換語を確認できます。" };
        argumentTips.SetToolTip(Arguments, EditorArguments.Help);
        root.Controls.Add(status, 0, 6); root.SetColumnSpan(status, 2);
        list.SelectionChanged += (_, _) => { if (!refreshing && EditingApplication is { } app) { appName.Text = app.Name; SetLaunch(app.Launch, app.Executable, app.Arguments); } };
        Refresh(selectedId);
        if (applications.Count > 0) SelectRow(applications[Math.Max(0, applications.FindIndex(a => a.Id == selectedId))].Id);

        static DataGridViewTextBoxColumn Column(string name, string heading, float fillWeight) =>
            new() { Name = name, HeaderText = heading, FillWeight = fillWeight, SortMode = DataGridViewColumnSortMode.NotSortable };

        void UpdateListHeight()
        {
            int rowHeight = Font.Height + LogicalToDeviceUnits(8);
            list.RowTemplate.Height = rowHeight;
            foreach (DataGridViewRow row in list.Rows) row.Height = rowHeight;
            list.ColumnHeadersHeight = rowHeight;
            root.RowStyles[1].Height = list.ColumnHeadersHeight + VisibleRows * rowHeight
                + list.Margin.Vertical + LogicalToDeviceUnits(2);
        }

        void Add(string label, Control control, int row)
        {
            root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Fill; root.Controls.Add(control, 1, row);
        }
        void Button(string text, string suffix, Action action)
        {
            var button = SettingsForm.MakeButton(text, prefix + "Editor" + suffix); button.Width = suffix == "Store" ? 132 : 84;
            button.Click += (_, _) => { try { action(); status.Text = "変更しました。設定の保存で確定します。"; } catch (ArgumentException e) { status.Text = e.Message; } };
            buttons.Controls.Add(button);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { newMenu.Dispose(); argumentTips.Dispose(); }
        base.Dispose(disposing);
    }
    private void Refresh(string id)
    {
        refreshing = true;
        try
        {
            list.Rows.Clear();
            foreach (var app in applications)
                list.Rows[list.Rows.Add(app.Name, "", "")].Tag = app;
            list.ClearSelection();
            selected.Items.Clear(); selected.Items.Add("なし"); selected.Items.AddRange(applications.Cast<object>().ToArray());
            selected.SelectedIndex = applications.FindIndex(a => a.Id == id) + 1;
        }
        finally { refreshing = false; }
        SelectedChanged?.Invoke(SelectedId);
        RefreshUsage();
    }

    private string? EditorId(CapturePreset preset) => intent == CaptureIntent.Screenshot ? preset.ImageEditorId : preset.VideoEditorId;

    internal void RefreshUsage()
    {
        var (presets, notificationId) = readUsage();
        bool notificationHasDirectApp = applications.Any(app => app.Id == notificationId);
        foreach (DataGridViewRow row in list.Rows)
        {
            var app = (ExternalApplication)row.Tag!;
            var linked = presets.Where(preset => EditorId(preset) == app.Id).ToArray();
            row.Cells[1].Value = app.Id == notificationId ? "直接指定"
                : !notificationHasDirectApp && linked.Length > 0 ? "プリセット経由" : "—";
            row.Cells[2].Value = linked.Length == 0 ? "—" : string.Join("、", linked.Select(DisplayName));
            row.Cells[2].ToolTipText = string.Join("\n", linked.Select(preset => DisplayName(preset)
                + (preset.Destination.HasFlag(OutputDestination.ExternalApp) ? "" : "（自動で開く：OFF）")));
        }

        static string DisplayName(CapturePreset preset) => (preset.IsDefault ? "★ " : "") + preset.Name;
    }

    private void SelectRow(string id)
    {
        int index = applications.FindIndex(app => app.Id == id);
        if (index < 0) return;
        list.CurrentCell = list.Rows[index].Cells[0];
        list.Rows[index].Selected = true;
    }
    private void SetLaunch(ExternalApplicationLaunch kind, string path, string arguments)
    {
        launch = kind;
        Executable.Text = path; Arguments.Text = arguments;
        Executable.ReadOnly = Arguments.ReadOnly = kind != ExternalApplicationLaunch.Executable;
        Executable.PlaceholderText = Executable.ReadOnly ? "WindowsのPNG関連付け" : "";
    }
    private void Store()
    {
        var prior = EditingApplication;
        var app = new ExternalApplication { Id = prior?.Id ?? Guid.NewGuid().ToString("N"), Name = appName.Text.Trim(),
            Launch = launch, Executable = Environment.ExpandEnvironmentVariables(Executable.Text.Trim().Trim('"')), Arguments = Arguments.Text };
        var next = applications.Where(a => a.Id != app.Id).Append(app).ToArray();
        ExternalApplication.ValidateList(next, "");
        string id = SelectedId;
        if (prior is not null) applications[applications.FindIndex(a => a.Id == prior.Id)] = app; else applications.Add(app);
        Refresh(string.IsNullOrEmpty(id) ? app.Id : id); SelectRow(app.Id);
    }
    private void Delete()
    {
        if (EditingApplication is not { } app) return;
        var (presets, notificationId) = readUsage();
        if (SelectedId == app.Id || notificationId == app.Id || presets.Any(preset => EditorId(preset) == app.Id))
            throw new ArgumentException("通知または出力プリセットで参照中です。先に指定を変更してください。");
        string id = SelectedId; applications.Remove(app); Refresh(id);
    }
}
