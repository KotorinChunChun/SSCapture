namespace SSCapture.UI;

internal static class SettingsEditorActions
{
    internal const int Height = 44;
    internal const int PresetListWidth = 264;
    internal const int PresetListGap = 12;

    internal static FlowLayoutPanel CreateRow(string name, Button add, Button duplicate, Button save, Button delete)
    {
        var row = new FlowLayoutPanel { Name = name, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        Button[] buttons = [add, duplicate, save, delete];
        for (int index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.AutoSize = false;
            button.Dock = DockStyle.None;
            button.Size = new Size(104, 36);
            button.Margin = new Padding(0, 4, index == buttons.Length - 1 ? 0 : 8, 4);
        }
        row.Controls.AddRange(buttons);
        return row;
    }

    internal static Button CreateButton(string text, string name, Action action, ButtonRole role = ButtonRole.Secondary)
    {
        var button = SettingsForm.MakeButton(text, name, role);
        button.Click += (_, _) => action();
        return button;
    }

    internal static void AddDefaultButton(TableLayoutPanel listColumn, string name, Action action)
    {
        var button = CreateButton("★既定にする", name, action);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 4, 0, 0);
        listColumn.Controls.Add(button, 0, 1);
    }
}