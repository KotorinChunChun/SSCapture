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
    private readonly Func<ExternalApplication[], string, string?>? saveApplications;
    private ExternalApplicationLaunch launch;
    internal event Action<string>? SelectedChanged;
    internal void SelectApplication(string id)
    {
        refreshing = true;
        try { selected.SelectedIndex = applications.FindIndex(a => a.Id == id) + 1; }
        finally { refreshing = false; }
    }

    internal ExternalApplicationsEditor(CaptureIntent intent, ExternalApplication[] apps, string selectedId, string legacyPath,
        string legacyArgs, IReadOnlyList<EditorPreset> examples, Func<(IReadOnlyList<CapturePreset> Presets, string NotificationId)> readUsage,
        Func<ExternalApplication[], string, string?>? saveApplications = null)
    {
        string prefix = intent == CaptureIntent.Screenshot ? "Image" : "Video";
        Name = prefix + "ApplicationsEditor"; Dock = DockStyle.Fill;
        this.intent = intent;
        this.readUsage = readUsage;
        this.saveApplications = saveApplications;
        applications = apps.ToList();
        if (apps.Length == 0 && !string.IsNullOrWhiteSpace(legacyPath))
        {
            var legacy = new ExternalApplication { Name = Path.GetFileNameWithoutExtension(legacyPath), Executable = legacyPath, Arguments = legacyArgs };
            applications.Add(legacy); selectedId = legacy.Id;
        }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 5; row++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsEditorActions.Height)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        var newButton = SettingsForm.MakeButton("追加 ▼", prefix + "EditorNew");
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
                status.Text = "新規入力です。［保存］で登録します。";
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
        var duplicate = SettingsEditorActions.CreateButton("複製", prefix + "EditorDuplicate", () =>
        {
            if (EditingApplication is null) return;
            string name = PresetSettings.UniqueName(appName.Text.Trim() + "のコピー", applications.Select(app => app.Name));
            list.ClearSelection();
            appName.Text = name;
            appName.Select();
        });
        var store = SettingsEditorActions.CreateButton("保存", prefix + "EditorStore", () => Edit(Store), ButtonRole.Primary);
        var delete = SettingsEditorActions.CreateButton("削除", prefix + "EditorDelete", () => Edit(Delete), ButtonRole.Danger);
        var buttons = SettingsEditorActions.CreateRow(prefix + "EditorActions", newButton, duplicate, store, delete);
        root.Controls.Add(buttons, 0, 5);
        root.SetColumnSpan(buttons, 2);
        status = new Label { Name = prefix + "EditorStatus", Dock = DockStyle.Fill };
        argumentTips.SetToolTip(Arguments, EditorArguments.Help);
        root.Controls.Add(status, 0, 6); root.SetColumnSpan(status, 2);
        list.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            var app = EditingApplication;
            duplicate.Enabled = delete.Enabled = app is not null;
            if (app is null) return;
            appName.Text = app.Name;
            SetLaunch(app.Launch, app.Executable, app.Arguments);
        };
        Refresh(selectedId);
        if (applications.Count > 0) SelectRow(applications[Math.Max(0, applications.FindIndex(a => a.Id == selectedId))].Id);
        duplicate.Enabled = delete.Enabled = EditingApplication is not null;

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
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { newMenu.Dispose(); argumentTips.Dispose(); }
        base.Dispose(disposing);
    }
    internal void LoadRestoredApplications(ExternalApplication[] restored)
    {
        string selectedId = SelectedId;
        string? editingId = EditingApplication?.Id;
        applications.Clear();
        applications.AddRange(restored);
        Refresh(selectedId, notifySelection: false);
        if (editingId is not null) SelectRow(editingId);
        else if (applications.Count > 0) SelectRow(applications[0].Id);
        else { appName.Clear(); SetLaunch(ExternalApplicationLaunch.Executable, "", EditorArguments.Default); }
        status.Text = "変更しました。設定の保存で確定します。";
    }
    private void Refresh(string id, bool notifySelection = true)
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
        if (notifySelection) SelectedChanged?.Invoke(SelectedId);
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
    private void Edit(Action update)
    {
        try { update(); }
        catch (ArgumentException ex) { status.Text = ex.Message; }
    }

    private void Store()
    {
        var prior = EditingApplication;
        var app = new ExternalApplication { Id = prior?.Id ?? Guid.NewGuid().ToString("N"), Name = appName.Text.Trim(), BuiltInKey = prior?.BuiltInKey,
            Launch = launch, Executable = Environment.ExpandEnvironmentVariables(Executable.Text.Trim().Trim('"')), Arguments = Arguments.Text };
        var next = applications.ToList();
        int index = next.FindIndex(candidate => candidate.Id == app.Id);
        if (index < 0) next.Add(app);
        else next[index] = app;
        string id = SelectedId;
        ApplyApplications(next.ToArray(), string.IsNullOrEmpty(id) ? app.Id : id, app.Id);
    }

    private void Delete()
    {
        if (EditingApplication is not { } app) return;
        var (presets, notificationId) = readUsage();
        if (SelectedId == app.Id || notificationId == app.Id || presets.Any(preset => EditorId(preset) == app.Id))
            throw new ArgumentException("通知または出力プリセットで参照中です。先に指定を変更してください。");
        ExternalApplication[] next = applications.Where(candidate => candidate.Id != app.Id).ToArray();
        ApplyApplications(next, SelectedId, next.FirstOrDefault()?.Id);
        if (next.Length == 0)
        {
            appName.Clear();
            SetLaunch(ExternalApplicationLaunch.Executable, "", EditorArguments.Default);
        }
    }
    private void ApplyApplications(ExternalApplication[] next, string selectedId, string? editingId)
    {
        ExternalApplication.ValidateList(next, selectedId);
        if (saveApplications?.Invoke(next, selectedId) is { } error) throw new ArgumentException(error);
        applications.Clear();
        applications.AddRange(next);
        Refresh(selectedId);
        if (editingId is not null) SelectRow(editingId);
        status.Text = saveApplications is null ? "一覧を更新しました。" : "外部アプリの設定を保存しました。";
    }
}
