using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class PresetCaptureShortcutEditor : UserControl
{
    private readonly List<PresetCaptureShortcut> rows;
    private readonly DataGridView list = new()
    {
        Name = "PresetCaptureShortcuts", Dock = DockStyle.Fill, ReadOnly = true, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(0, 4, 0, 4)
        }
    };
    private readonly HotkeyEditor key = new("PresetCaptureShortcut", "") { Dock = DockStyle.Fill, AccessibleName = "ショートカットキー" };
    private readonly ComboBox intent = PresetChoice("ShortcutCaptureIntent", "撮影種別");
    private readonly ComboBox input = PresetChoice("ShortcutInputPreset", "入力プリセット");
    private readonly ComboBox output = PresetChoice("ShortcutOutputPreset", "出力プリセット");
    private readonly Button duplicate, remove;
    private PresetItem[] inputs = [], outputs = [];
    private string? defaultInput, defaultOutput;
    private bool loading;
    private int selected = -1;
    private string? initialInput, initialOutput;

    internal PresetCaptureShortcutEditor(PresetCaptureShortcut[] shortcuts, Action save)
    {
        Dock = DockStyle.Fill;
        rows = shortcuts.ToList();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);
        layout.Controls.Add(list, 0, 0);

        var group = new GroupBox { Name = "SelectedShortcutAssignment", Text = "選択した割り当ての編集", Dock = DockStyle.Fill,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 8) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 5, Margin = Padding.Empty };
        foreach (int width in new[] { 20, 40, 40 })
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsEditorActions.Height));
        group.Controls.Add(fields);
        layout.Controls.Add(group, 0, 1);

        string[] labels = ["ショートカットキー", "撮影種別", "入力プリセット", "出力プリセット"];
        for (int column = 0; column < labels.Length; column++)
        {
            int index = list.Columns.Add("PresetCaptureColumn" + column, labels[column]);
            list.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
            fields.Controls.Add(new Label { Name = "PresetCaptureFieldLabel" + column, Text = labels[column], AutoSize = true },
                Math.Max(0, column - 1), column == 0 ? 0 : 2);
        }
        fields.Controls.Add(key, 0, 1);
        fields.SetColumnSpan(key, 3);
        intent.Items.AddRange(["静止画", "録画"]);
        fields.Controls.Add(intent, 0, 3);
        fields.Controls.Add(input, 1, 3);
        fields.Controls.Add(output, 2, 3);

        var add = SettingsEditorActions.CreateButton("追加", "AddPresetCaptureShortcut",
            () => AddDraft(new("", initialInput, initialOutput) { SelectRangeWithDefaultInput = true }));
        duplicate = SettingsEditorActions.CreateButton("複製", "DuplicatePresetCaptureShortcut", () =>
        {
            if (selected >= 0) AddDraft(rows[selected] with { Key = "" });
        });
        var store = SettingsEditorActions.CreateButton("保存", "SavePresetCaptureShortcuts", save, ButtonRole.Primary);
        remove = SettingsEditorActions.CreateButton("削除", "DeletePresetCaptureShortcut", () =>
        {
            if (selected < 0) return;
            rows.RemoveAt(selected);
            RefreshRows(Math.Min(selected, rows.Count - 1));
        }, ButtonRole.Danger);
        var buttons = SettingsEditorActions.CreateRow("ShortcutAssignmentActions", add, duplicate, store, remove);
        fields.Controls.Add(buttons, 0, 4);
        fields.SetColumnSpan(buttons, 3);

        list.SelectionChanged += (_, _) =>
        {
            if (!loading) SelectRow(list.SelectedRows.Count == 1 ? list.SelectedRows[0].Index : -1);
        };
        key.ValueChanged += (_, _) => UpdateRow();
        intent.SelectedIndexChanged += (_, _) => UpdateRow();
        input.SelectedIndexChanged += (_, _) => UpdateRow();
        output.SelectedIndexChanged += (_, _) => UpdateRow();
        RefreshRows(rows.Count > 0 ? 0 : -1);
    }

    private void AddDraft(PresetCaptureShortcut row)
    {
        rows.Add(row);
        RefreshRows(rows.Count - 1);
        key.SelectNextControl(null, true, true, true, false);
    }

    internal event EventHandler? DraftChanged;

    internal PresetCaptureShortcut[] Rows => rows.ToArray();

    internal void LoadRows(PresetCaptureShortcut[] replacements)
    {
        rows.Clear();
        rows.AddRange(replacements);
        RefreshRows(rows.Count > 0 ? 0 : -1);
    }

    internal void RefreshPresets(SavedRegion[] inputPresets, CapturePreset[] outputPresets)
    {
        inputs = inputPresets.Select(p => new PresetItem(p.Id, p.Name)).ToArray();
        outputs = outputPresets.Select(p => new PresetItem(p.Id, p.Name)).ToArray();
        defaultInput = inputPresets.FirstOrDefault(p => p.IsDefault)?.Id;
        defaultOutput = outputPresets.FirstOrDefault(p => p.IsDefault)?.Id;
        RefreshRows(selected);
    }

    internal void OpenForPreset(string? inputId, string? outputId)
    {
        initialInput = inputId;
        initialOutput = outputId;
        int index = rows.FindIndex(row => inputId is not null ? row.InputPresetId == inputId : outputId is not null && row.OutputPresetId == outputId);
        if (index < 0 && inputId is not null && inputId == defaultInput)
            index = rows.FindIndex(row => row.InputPresetId is null);
        if (index < 0 && outputId is not null && outputId == defaultOutput)
            index = rows.FindIndex(row => row.OutputPresetId is null);
        RefreshRows(index >= 0 ? index : selected);
    }

    private void RefreshRows(int index)
    {
        loading = true;
        try
        {
            list.Rows.Clear();
            foreach (var row in rows)
            {
                int rowIndex = list.Rows.Add(DisplayValues(row));
                SetTooltips(list.Rows[rowIndex], row);
            }
            list.CurrentCell = index >= 0 ? list.Rows[index].Cells[0] : null;
            if (index < 0) list.ClearSelection();
        }
        finally { loading = false; }
        SelectRow(index);
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectRow(int index)
    {
        selected = index;
        loading = true;
        try
        {
            var row = index >= 0 ? rows[index] : null;
            duplicate.Enabled = remove.Enabled = key.Enabled = intent.Enabled = input.Enabled = output.Enabled = row is not null;
            key.Value = row?.Key ?? "";
            intent.SelectedIndex = row is null ? -1 : (int)row.Intent;
            SetChoices(input, inputs, row?.InputPresetId);
            SetChoices(output, outputs, row?.OutputPresetId);
        }
        finally { loading = false; }
    }

    private void UpdateRow()
    {
        if (loading || selected < 0) return;
        var previous = rows[selected];
        string? outputId = (output.SelectedItem as PresetItem)?.Id;
        string? inputId = (input.SelectedItem as PresetItem)?.Id;
        var captureIntent = (CaptureIntent)intent.SelectedIndex;
        var row = previous with
        {
            Key = key.Value, InputPresetId = inputId, OutputPresetId = outputId, Intent = captureIntent,
            LegacyInstantOutput = previous.LegacyInstantOutput && captureIntent == CaptureIntent.Screenshot && outputId is null
                && (inputId is not null || !previous.SelectRangeWithDefaultInput)
        };
        rows[selected] = row;
        var values = DisplayValues(row);
        for (int column = 0; column < values.Length; column++) list.Rows[selected].Cells[column].Value = values[column];
        SetTooltips(list.Rows[selected], row);
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetTooltips(DataGridViewRow displayRow, PresetCaptureShortcut row)
    {
        displayRow.Cells[2].ToolTipText = row.UsesNormalSelection ? "既定が固定座標でも選択画面を開きます。" : "";
        displayRow.Cells[3].ToolTipText = row.LegacyInstantOutput ? "旧固定位置キーの即時出力（コピーとファイル保存）" : "";
    }

    private object[] DisplayValues(PresetCaptureShortcut row) =>
        [KeyDisplay.Binding(row.Key), row.IntentLabel,
            row.InputPresetId is null ? "★既定を使う" : FindItem(inputs, row.InputPresetId).Name,
            row.OutputPresetId is null ? "★既定を使う" : FindItem(outputs, row.OutputPresetId).Name];

    private static void SetChoices(ComboBox combo, PresetItem[] choices, string? id)
    {
        combo.Items.Clear();
        combo.Items.Add(FindItem(choices, null));
        combo.Items.AddRange(choices);
        PresetItem selectedItem = FindItem(choices, id);
        if (!combo.Items.Contains(selectedItem)) combo.Items.Add(selectedItem);
        combo.SelectedItem = selectedItem;
    }

    private static PresetItem FindItem(PresetItem[] choices, string? id) => id is null ? new(null, "既定を使う")
        : choices.FirstOrDefault(p => p.Id == id) ?? new(id, "未登録: " + id);

    private static ComboBox PresetChoice(string name, string label) => new()
    {
        Name = name, AccessibleName = label, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList
    };

    private sealed record PresetItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
}
