using SSCapture.Capture;

namespace SSCapture.UI;

internal sealed class CaptureKeyEditor : UserControl
{
    private readonly List<ComboBox> commands = [];
    private readonly CheckBox space;
    private sealed record Choice(CaptureKeyAction Action)
    {
        public override string ToString() => CaptureKeyMap.Describe(Action);
    }

    internal CaptureKeyEditor(CaptureKeyMap map)
    {
        Name = "CaptureKeyEditor";
        Dock = DockStyle.Fill;
        var table = new TableLayoutPanel { ColumnCount = 4, RowCount = 6, Dock = DockStyle.Top, AutoSize = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int row = 0; row < 6; row++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        Controls.Add(table);
        foreach (var keys in CaptureKeyMap.Combinations)
        {
            int index = commands.Count;
            int column = index < 5 ? 0 : 2;
            int row = index % 5;
            string label = CaptureKeyMap.Label(keys);
            var combo = new ComboBox { Name = "CaptureKeys" + (int)keys, AccessibleName = label,
                DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach (CaptureKeyAction action in Enum.GetValues<CaptureKeyAction>()) combo.Items.Add(new Choice(action));
            combo.SelectedIndex = (int)map.Resolve(keys);
            commands.Add(combo);
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, column, row);
            table.Controls.Add(combo, column + 1, row);
        }
        space = new CheckBox { Name = "CaptureKeysSpace", Text = "Spaceで基点を切り替える", AutoSize = true,
            Anchor = AnchorStyles.Left, Checked = map.SpaceEnabled };
        table.Controls.Add(space, 0, 5);
        table.SetColumnSpan(space, 4);
    }

    internal CaptureKeyMap Value => new(ActionAt(0), ActionAt(1), ActionAt(2), ActionAt(3), ActionAt(4),
        ActionAt(5), ActionAt(6), ActionAt(7), ActionAt(8), ActionAt(9), space.Checked);

    private CaptureKeyAction ActionAt(int index) => ((Choice)commands[index].SelectedItem!).Action;
}
