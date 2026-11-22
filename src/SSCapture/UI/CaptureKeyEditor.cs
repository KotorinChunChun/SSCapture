using SSCapture.Capture;

namespace SSCapture.UI;

internal sealed class CaptureKeyEditor : UserControl
{
    private static readonly CaptureModifiers[] Modifiers =
        [CaptureModifiers.Ctrl, CaptureModifiers.Shift, CaptureModifiers.Alt, CaptureModifiers.Win];
    private readonly Dictionary<CaptureModifiers, ComboBox> commands = [];
    private readonly Dictionary<MouseButtons, ComboBox> clicks = [];
    private readonly CheckBox? space;
    private readonly ComboBox? controlActivation, windowActivation;

    private sealed record Choice(CaptureKeyAction Action, string Text)
    {
        public override string ToString() => Text;
    }

    internal CaptureKeyEditor(CaptureKeyMap map, CaptureMouseMap mouse, bool beforeSelection,
        CaptureActivation initialControlActivation = CaptureActivation.Push, CaptureActivation initialWindowActivation = CaptureActivation.Push)
    {
        string prefix = beforeSelection ? "CaptureBeforeKeys" : "CaptureKeys";
        Name = prefix + "Editor";
        Dock = DockStyle.Fill;
        AutoScroll = true;
        var table = new TableLayoutPanel { Name = prefix + "Grid", ColumnCount = 5,
            RowCount = beforeSelection ? 10 : 9, Dock = DockStyle.Top };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int column = 0; column < Modifiers.Length; column++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (int row = 0; row < table.RowCount; row++) table.RowStyles.Add(new RowStyle(SizeType.Absolute));
        Controls.Add(table);

        Choice[] choices = Enum.GetValues<CaptureKeyAction>()
            .Where(action => beforeSelection ? CaptureKeyMap.IsBeforeAction(action) : action != CaptureKeyAction.HighlightWindow)
            .Select(action => new Choice(action, action switch
            {
                CaptureKeyAction.SwitchAnchor when beforeSelection => "基点切替（固定サイズ）",
                _ => CaptureKeyMap.Describe(action)
            })).ToArray();

        for (int index = 0; index < Modifiers.Length; index++)
        {
            string label = CaptureKeyMap.Label(Modifiers[index]);
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.None }, index + 1, 0);
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index + 1);
        }

        for (int row = 0; row < Modifiers.Length; row++)
        {
            for (int column = row; column < Modifiers.Length; column++)
            {
                CaptureModifiers keys = Modifiers[row] | Modifiers[column];
                int selected = Array.FindIndex(choices, choice => choice.Action == map.Resolve(keys));
                string name = prefix + (int)keys;
                string label = CaptureKeyMap.Label(keys);
                var primary = AddChoice(table, column + 1, row + 1, name, label, choices, Math.Max(0, selected));
                commands.Add(keys, primary);
                if (column == row) continue;

                var mirror = AddChoice(table, row + 1, column + 1, name + "Mirror", label, choices, primary.SelectedIndex);
                primary.SelectedIndexChanged += (_, _) => mirror.SelectedIndex = primary.SelectedIndex;
                mirror.SelectedIndexChanged += (_, _) => primary.SelectedIndex = mirror.SelectedIndex;
            }
        }

        int clickRow = 5;
        if (!beforeSelection)
        {
            space = new CheckBox { Name = "CaptureKeysSpace", Text = "Spaceで基点を切り替える", AutoSize = true,
                Anchor = AnchorStyles.Left, Checked = map.SpaceEnabled, Margin = new Padding(3, 6, 3, 6) };
            table.Controls.Add(space, 1, clickRow++);
            table.SetColumnSpan(space, 4);
        }

        if (beforeSelection)
        {
            controlActivation = AddActivation(clickRow++, "ControlActivation", "コントロール選択", initialControlActivation);
            windowActivation = AddActivation(clickRow++, "WindowActivation", "ウィンドウ撮影", initialWindowActivation);
            AddClick(MouseButtons.Left, clickRow, "BeforeLeft", "左クリック", "ウィンドウ撮影／ドラッグ開始（固定サイズでは確定）", mouse.BeforeLeft);
            AddClick(MouseButtons.Right, clickRow + 1, "BeforeRight", "右クリック", "撮影メニューを開く", mouse.BeforeRight);
            AddClick(MouseButtons.Middle, clickRow + 2, "BeforeMiddle", "中クリック", "ポインタ下のモニターを撮影", mouse.BeforeMiddle);
        }
        else
        {
            AddClick(MouseButtons.Left, clickRow, "DragLeft", "左クリック", "使用不可", false, enabled: false);
            AddClick(MouseButtons.Right, clickRow + 1, "DragRight", "右クリック", "ドラッグを中止", mouse.DragRight);
            AddClick(MouseButtons.Middle, clickRow + 2, "DragMiddle", "中クリック", "なし", false, enabled: false);
        }
        SizeRows(table);
        table.FontChanged += (_, _) => SizeRows(table);
        table.DpiChangedAfterParent += (_, _) => SizeRows(table);

        ComboBox AddActivation(int row, string name, string label, CaptureActivation activation)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            var combo = AddChoice(table, 1, row, name, label, ["押している間だけ（プッシュ）", "単押しで切り替え（トグル）"], (int)activation);
            table.SetColumnSpan(combo, 4);
            return combo;
        }

        void AddClick(MouseButtons button, int row, string name, string label, string action, bool assigned, bool enabled = true)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            var combo = AddChoice(table, 1, row, "CaptureMouse" + name, label,
                enabled ? ["なし", action] : [action], assigned ? 1 : 0, enabled);
            table.SetColumnSpan(combo, 4);
            clicks.Add(button, combo);
        }
    }

    internal CaptureKeyMap Value => new(
        ActionAt(CaptureModifiers.Ctrl), ActionAt(CaptureModifiers.Shift), ActionAt(CaptureModifiers.Alt), ActionAt(CaptureModifiers.Win),
        ActionAt(CaptureModifiers.Ctrl | CaptureModifiers.Shift), ActionAt(CaptureModifiers.Ctrl | CaptureModifiers.Alt),
        ActionAt(CaptureModifiers.Ctrl | CaptureModifiers.Win), ActionAt(CaptureModifiers.Shift | CaptureModifiers.Alt),
        ActionAt(CaptureModifiers.Shift | CaptureModifiers.Win), ActionAt(CaptureModifiers.Alt | CaptureModifiers.Win), space?.Checked ?? false);

    internal bool IsClickEnabled(MouseButtons button) => clicks[button].SelectedIndex == 1;
    internal CaptureActivation ControlActivation => (CaptureActivation)(controlActivation?.SelectedIndex ?? 0);
    internal CaptureActivation WindowActivation => (CaptureActivation)(windowActivation?.SelectedIndex ?? 0);

    private CaptureKeyAction ActionAt(CaptureModifiers keys) => ((Choice)commands[keys].SelectedItem!).Action;

    private static void SizeRows(TableLayoutPanel table)
    {
        var heights = new int[table.RowCount];
        foreach (Control control in table.Controls)
        {
            int row = table.GetRow(control);
            int height = control is ComboBox combo ? Math.Max(combo.Height, combo.PreferredHeight)
                : control.GetPreferredSize(Size.Empty).Height;
            heights[row] = Math.Max(heights[row], height + control.Margin.Vertical);
        }
        table.SuspendLayout();
        for (int row = 0; row < heights.Length; row++) table.RowStyles[row].Height = heights[row];
        table.Padding = new Padding(0, 0, 0, table.LogicalToDeviceUnits(4));
        table.Height = heights.Sum() + table.Padding.Vertical;
        table.ResumeLayout();
    }

    private static ComboBox AddChoice(TableLayoutPanel table, int column, int row, string name, string label,
        object[] items, int selected, bool enabled = true)
    {
        var combo = new ComboBox { Name = name, AccessibleName = label, DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Enabled = enabled };
        combo.Items.AddRange(items);
        combo.SelectedIndex = selected;
        combo.DropDown += (_, _) => combo.DropDownWidth = Math.Max(combo.Width,
            combo.Items.Cast<object>().Max(item => TextRenderer.MeasureText(item.ToString(), combo.Font).Width)
            + SystemInformation.VerticalScrollBarWidth + 8);
        table.Controls.Add(combo, column, row);
        return combo;
    }
}
