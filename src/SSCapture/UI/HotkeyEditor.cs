using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class HotkeyEditor : UserControl
{
    private readonly CheckBox win, ctrl, alt, shift;
    private readonly ComboBox key;
    private bool settingValue;
    private bool initialized, arranging;
    private string notifiedValue = "";
    public event EventHandler? ValueChanged;
    public HotkeyEditor(string name, string value)
    {
        Name = name;
        AutoScaleMode = AutoScaleMode.Dpi;
        Margin = Padding.Empty;
        win = Modifier("Win"); ctrl = Modifier("Ctrl"); alt = Modifier("Alt"); shift = Modifier("Shift");
        key = new ComboBox { Name = name + "Key", DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Top | AnchorStyles.Left, MinimumSize = new Size(120, 0), Margin = Padding.Empty };
        key.Items.Add(new KeyItem(null, "なし"));
        var seen = new HashSet<int>();
        foreach (Keys code in Enum.GetValues<Keys>())
        {
            try
            {
                var binding = HotkeyBinding.Parse(code.ToString());
                if (binding is { } valid && valid.Modifiers == HotkeyModifiers.None && seen.Add(valid.VirtualKey))
                    key.Items.Add(new KeyItem(valid.VirtualKey, valid.ToString()));
            }
            catch (ArgumentException) { }
        }
        Controls.AddRange([win, ctrl, alt, shift, key]);
        foreach (Control child in Controls)
        {
            child.SizeChanged += (_, _) => ArrangeControls();
            child.FontChanged += (_, _) => ArrangeControls();
            child.HandleCreated += (_, _) => ArrangeControls();
        }
        key.SelectedIndexChanged += (_, _) =>
        {
            bool enabled = ((KeyItem?)key.SelectedItem)?.Code is not null;
            win.Enabled = ctrl.Enabled = alt.Enabled = shift.Enabled = enabled;
            NotifyValueChanged();
        };
        foreach (CheckBox modifier in new[] { win, ctrl, alt, shift }) modifier.CheckedChanged += (_, _) => NotifyValueChanged();
        initialized = true;
        ArrangeControls();
        Value = value;
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get
        {
            if (key.SelectedItem is not KeyItem { Code: { } code }) return "";
            var modifiers = (win.Checked ? HotkeyModifiers.Windows : 0) | (ctrl.Checked ? HotkeyModifiers.Control : 0) |
                (alt.Checked ? HotkeyModifiers.Alt : 0) | (shift.Checked ? HotkeyModifiers.Shift : 0);
            return new HotkeyBinding(code, modifiers).ToString();
        }
        set
        {
            var binding = HotkeyBinding.Parse(value);
            int index = Enumerable.Range(0, key.Items.Count).FirstOrDefault(i => ((KeyItem)key.Items[i]!).Code == binding?.VirtualKey, -1);
            if (index < 0) throw new ArgumentException("選択できないキーです。");
            settingValue = true;
            try
            {
                win.Checked = binding?.Modifiers.HasFlag(HotkeyModifiers.Windows) == true;
                ctrl.Checked = binding?.Modifiers.HasFlag(HotkeyModifiers.Control) == true;
                alt.Checked = binding?.Modifiers.HasFlag(HotkeyModifiers.Alt) == true;
                shift.Checked = binding?.Modifiers.HasFlag(HotkeyModifiers.Shift) == true;
                key.SelectedIndex = index;
            }
            finally { settingValue = false; }
            NotifyValueChanged();
        }
    }
    private static CheckBox Modifier(string text)
    {
        return new CheckBox { Text = text, Name = text, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Margin = new Padding(0, 0, 10, 0), TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft };
    }

    protected override void OnLayout(LayoutEventArgs e)
    { base.OnLayout(e); ArrangeControls(); }

    private void ArrangeControls()
    {
        if (!initialized || arranging) return;
        arranging = true;
        try
        {
            int left = 0;
            foreach (CheckBox modifier in new[] { win, ctrl, alt, shift })
            {
                modifier.Size = modifier.GetPreferredSize(Size.Empty);
                modifier.Location = new Point(left, Math.Max(0, (ClientSize.Height - modifier.Height) / 2));
                left += modifier.Width + modifier.Margin.Right;
            }
            // The native drop-down may change its actual height after font or handle
            // creation. Position only after applying its native preferred height;
            // SizeChanged repeats this calculation when that height changes later.
            key.Size = new Size(Math.Max(key.MinimumSize.Width, ClientSize.Width - left), key.PreferredHeight);
            key.Location = new Point(left, Math.Max(0, (ClientSize.Height - key.Height) / 2));
        }
        finally { arranging = false; }
    }
    private void NotifyValueChanged()
    {
        if (settingValue) return;
        string value = Value;
        if (value == notifiedValue) return;
        notifiedValue = value;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
    private sealed record KeyItem(int? Code, string Label) { public override string ToString() => Label; }
}
