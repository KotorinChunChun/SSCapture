using SSCapture.Capture;

namespace SSCapture.UI;

internal sealed class CaptureKeyEditor : UserControl
{
    private static readonly CaptureModifiers[] Modifiers =
        [CaptureModifiers.Ctrl, CaptureModifiers.Shift, CaptureModifiers.Alt, CaptureModifiers.Win];
    private readonly Dictionary<CaptureModifiers, ComboBox> commands = [];
    private readonly Dictionary<MouseButtons, ComboBox> clicks = [];
    private readonly ComboBox? space;
    private readonly CheckBox? wheelAdjust;
    private CaptureAnchorCycle loadedSpaceCycle;
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
            RowCount = 10, Dock = DockStyle.Top };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int column = 0; column < Modifiers.Length; column++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (int row = 0; row < table.RowCount; row++) table.RowStyles.Add(new RowStyle(SizeType.Absolute));
        Controls.Add(table);

        Choice[] choices = Enum.GetValues<CaptureKeyAction>()
            .Where(action => beforeSelection ? CaptureKeyMap.IsBeforeAction(action) : CaptureKeyMap.IsDragAction(action))
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

                var mirror = AddChoice(table, row + 1, column + 1, name + "Mirror", label, choices, primary.SelectedIndex, enabled: false);
                primary.SelectedIndexChanged += (_, _) => mirror.SelectedIndex = primary.SelectedIndex;
            }
        }

        int clickRow = 5;
        if (!beforeSelection)
        {
            table.Controls.Add(new Label { Text = "Space", AutoSize = true, Anchor = AnchorStyles.Left }, 0, clickRow);
            loadedSpaceCycle = map.SpaceCycle;
            space = AddChoice(table, 1, clickRow++, "CaptureKeysSpace", "Space",
                ["なし", "対角移動", "四方移動", "四方と中心"], SpaceIndex(map));
            table.SetColumnSpan(space, 4);
            wheelAdjust = new CheckBox { Name = "CaptureKeysWheelAdjustRectangle", Text = "ホイールで撮影範囲を調整する",
                AutoSize = true, Anchor = AnchorStyles.Left, Checked = map.WheelAdjustRectangle };
            table.Controls.Add(wheelAdjust, 1, clickRow++);
            table.SetColumnSpan(wheelAdjust, 4);
        }

        if (beforeSelection)
        {
            controlActivation = AddActivation(clickRow++, "ControlActivation", "コントロール選択", initialControlActivation);
            windowActivation = AddActivation(clickRow++, "WindowActivation", "ウィンドウ撮影", initialWindowActivation);
            AddClick(MouseButtons.Left, clickRow++, "BeforeLeft");
            AddClick(MouseButtons.Right, clickRow++, "BeforeRight");
            AddClick(MouseButtons.Middle, clickRow, "BeforeMiddle");
        }
        else
        {
            AddClick(MouseButtons.Left, clickRow++, "DragLeft", enabled: false);
            AddClick(MouseButtons.Right, clickRow++, "DragRight");
            AddClick(MouseButtons.Middle, clickRow, "DragMiddle");
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

        void AddClick(MouseButtons button, int row, string name, bool enabled = true)
        {
            string label = CaptureMouseMap.Label(button);
            table.Controls.Add(new Label { Name = "CaptureMouse" + name + "Label", Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            ClickChoice[] options = !enabled ? [new(CaptureClickAction.None, "使用不可")]
                : Enum.GetValues<CaptureClickAction>()
                    .Where(action => beforeSelection ? CaptureMouseMap.ValidBefore(action) : CaptureMouseMap.ValidDuring(action))
                    .Select(action => new ClickChoice(action, action switch
                    {
                        CaptureClickAction.Select => "撮影対象を選択／確定",
                        CaptureClickAction.Menu => "撮影メニューを開く",
                        CaptureClickAction.Desktop => "ポインタ下のモニターを撮影",
                        CaptureClickAction.Cancel => "ドラッグを中止",
                        _ => "なし"
                    })).ToArray();
            var initial = beforeSelection ? mouse.Before(button) : mouse.During(button);
            var combo = AddChoice(table, 1, row, "CaptureMouse" + name, label,
                options, Array.FindIndex(options, choice => choice.Action == initial), enabled);
            table.SetColumnSpan(combo, 4);
            clicks.Add(button, combo);
        }
    }

    private sealed record ClickChoice(CaptureClickAction Action, string Text)
    {
        public override string ToString() => Text;
    }

    internal void ResetDraft(CaptureKeyMap map, CaptureMouseMap mouse, CaptureActivation controlMode, CaptureActivation windowMode)
    {
        foreach (var (keys, combo) in commands)
            combo.SelectedItem = combo.Items.Cast<Choice>().FirstOrDefault(choice => choice.Action == map.Resolve(keys)) ?? combo.Items[0];

        loadedSpaceCycle = map.SpaceCycle;
        if (space is not null) space.SelectedIndex = SpaceIndex(map);
        if (wheelAdjust is not null) wheelAdjust.Checked = map.WheelAdjustRectangle;
        foreach (var (button, combo) in clicks)
        {
            var action = space is null ? mouse.Before(button) : mouse.During(button);
            combo.SelectedItem = combo.Items.Cast<ClickChoice>().First(choice => choice.Action == action);
        }
        if (controlActivation is not null) controlActivation.SelectedIndex = (int)controlMode;
        if (windowActivation is not null) windowActivation.SelectedIndex = (int)windowMode;
    }

    internal CaptureKeyMap Value => new(
        ActionAt(CaptureModifiers.Ctrl), ActionAt(CaptureModifiers.Shift), ActionAt(CaptureModifiers.Alt), ActionAt(CaptureModifiers.Win),
        ActionAt(CaptureModifiers.Ctrl | CaptureModifiers.Shift), ActionAt(CaptureModifiers.Ctrl | CaptureModifiers.Alt),
        ActionAt(CaptureModifiers.Ctrl | CaptureModifiers.Win), ActionAt(CaptureModifiers.Shift | CaptureModifiers.Alt),
        ActionAt(CaptureModifiers.Shift | CaptureModifiers.Win), ActionAt(CaptureModifiers.Alt | CaptureModifiers.Win), space?.SelectedIndex > 0, SelectedSpaceCycle, wheelAdjust?.Checked ?? true);

    private static int SpaceIndex(CaptureKeyMap map) => !map.SpaceEnabled ? 0 : Math.Max(1, (int)map.SpaceCycle);

    private CaptureAnchorCycle SelectedSpaceCycle => space?.SelectedIndex switch
    {
        null or 0 => loadedSpaceCycle,
        1 when loadedSpaceCycle == CaptureAnchorCycle.Legacy => loadedSpaceCycle,
        int index => (CaptureAnchorCycle)index
    };

    internal CaptureClickAction ClickAt(MouseButtons button) => ((ClickChoice)clicks[button].SelectedItem!).Action;
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
