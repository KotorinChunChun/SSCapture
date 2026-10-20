using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class ExternalApplicationsEditor : UserControl
{
    private readonly List<ExternalApplication> applications;
    private readonly ComboBox selected;
    private readonly ListBox list;
    private readonly TextBox appName;
    internal TextBox Executable { get; }
    internal TextBox Arguments { get; }
    private readonly Label status = null!;
    private readonly Func<IEnumerable<string?>> references;
    internal ExternalApplication[] Applications => applications.ToArray();
    internal string SelectedId => (selected.SelectedItem as ExternalApplication)?.Id ?? "";
    internal ExternalApplication? SelectedApplication => selected.SelectedItem as ExternalApplication;

    internal ExternalApplicationsEditor(string prefix, ExternalApplication[] apps, string selectedId, string legacyPath,
        string legacyArgs, IReadOnlyList<EditorPreset> examples, Func<IEnumerable<string?>> references)
    {
        Name = prefix + "ApplicationsEditor"; Dock = DockStyle.Fill;
        this.references = references;
        applications = apps.ToList();
        if (apps.Length == 0 && !string.IsNullOrWhiteSpace(legacyPath))
        {
            var legacy = new ExternalApplication { Name = Path.GetFileNameWithoutExtension(legacyPath), Executable = legacyPath, Arguments = legacyArgs };
            applications.Add(legacy); selectedId = legacy.Id;
        }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 6; row++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);
        selected = new ComboBox { Name = prefix + "SelectedEditor", DropDownStyle = ComboBoxStyle.DropDownList };
        Add("既定のアプリ", selected, 0);
        list = new ListBox { Name = prefix + "EditorList", IntegralHeight = false, Dock = DockStyle.Fill };
        root.Controls.Add(list, 0, 1); root.SetColumnSpan(list, 2); root.SetRowSpan(list, 2);
        appName = new TextBox { Name = prefix + "EditorName", MaxLength = 80 }; Add("登録名", appName, 3);
        Executable = new TextBox { Name = prefix + "EditorPath", Text = legacyPath }; Add("実行ファイル", Executable, 4);
        Arguments = new TextBox { Name = prefix + "EditorArguments", Text = legacyArgs }; Add("引数", Arguments, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 2);
        Button("新規", "New", () => { list.ClearSelected(); appName.Clear(); Executable.Clear(); Arguments.Text = EditorArguments.Default; });
        Button("複製", "Duplicate", () => { list.ClearSelected(); appName.Text += " コピー"; });
        Button("登録・更新", "Store", Store);
        Button("削除", "Delete", Delete);
        var example = new ComboBox { Name = prefix + "EditorPreset", DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var item in examples) example.Items.Add(item);
        example.SelectedIndex = 0;
        example.SelectedIndexChanged += (_, _) => { if (example.SelectedItem is EditorPreset { Executable.Length: > 0 } item) { Executable.Text = item.Executable; Arguments.Text = item.Arguments; } };
        buttons.Controls.Add(example);
        status = new Label { Name = prefix + "EditorStatus", Dock = DockStyle.Fill, Text = "登録・更新後、設定の保存で確定。{file} 完全パス / {directory} フォルダ / {filename} ファイル名" };
        root.Controls.Add(status, 0, 7); root.SetColumnSpan(status, 2);
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is ExternalApplication app) { appName.Text = app.Name; Executable.Text = app.Executable; Arguments.Text = app.Arguments; } };
        Refresh(selectedId);
        if (applications.Count > 0) list.SelectedIndex = Math.Max(0, applications.FindIndex(a => a.Id == selectedId));

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
    private void Refresh(string id)
    {
        list.Items.Clear(); list.Items.AddRange(applications.Cast<object>().ToArray());
        selected.Items.Clear(); selected.Items.Add("なし"); selected.Items.AddRange(applications.Cast<object>().ToArray());
        selected.SelectedIndex = applications.FindIndex(a => a.Id == id) + 1;
    }
    private void Store()
    {
        var prior = list.SelectedItem as ExternalApplication;
        var app = new ExternalApplication { Id = prior?.Id ?? Guid.NewGuid().ToString("N"), Name = appName.Text.Trim(),
            Executable = Environment.ExpandEnvironmentVariables(Executable.Text.Trim().Trim('"')), Arguments = Arguments.Text };
        var next = applications.Where(a => a.Id != app.Id).Append(app).ToArray();
        ExternalApplication.ValidateList(next, "");
        string id = SelectedId;
        if (prior is not null) applications[applications.FindIndex(a => a.Id == prior.Id)] = app; else applications.Add(app);
        Refresh(string.IsNullOrEmpty(id) ? app.Id : id); list.SelectedItem = app;
    }
    private void Delete()
    {
        if (list.SelectedItem is not ExternalApplication app) return;
        if (SelectedId == app.Id || references().Contains(app.Id)) throw new ArgumentException("既定または出力プリセットで参照中です。先に選択を変更してください。");
        string id = SelectedId; applications.Remove(app); Refresh(id);
    }
}
