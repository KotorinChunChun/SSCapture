using System.Runtime.CompilerServices;

namespace SSCapture.UI;

internal enum ButtonRole { Primary, Secondary, Neutral, Danger, Pause }

internal static class ButtonTheme
{
    private sealed class State
    {
        public ButtonRole Role;
        public bool FollowChecked;
    }

    private readonly record struct Palette(int Background, int Foreground, int Border, int Hover, int Pressed);
    private static readonly ConditionalWeakTable<ButtonBase, State> States = new();
    private static readonly Palette Primary = new(0x006874, 0xFFFFFF, 0x00535F, 0x005966, 0x004A55);
    private static readonly Palette Secondary = new(0xE3F0F6, 0x174A63, 0xA8C6D6, 0xD2E7F1, 0xC1DDEB);
    private static readonly Palette Neutral = new(0xECEFF1, 0x3B4852, 0xBCC7CD, 0xE0E5E9, 0xD2DADF);
    private static readonly Palette Danger = new(0xFCE8E6, 0x9C2F2F, 0xD99E9A, 0xF9D5D1, 0xF3BFBA);
    private static readonly Palette Pause = new(0xFFF0CF, 0x764500, 0xD9AA51, 0xFFE4AB, 0xFFD987);
    private static readonly Palette Disabled = new(0xF1F3F5, 0x626B73, 0xCAD0D5, 0xF1F3F5, 0xF1F3F5);

    public static void Apply(ButtonBase button, ButtonRole role)
    {
        State state = States.GetValue(button, Attach);
        state.Role = role;
        state.FollowChecked = false;
        Refresh(button, state);
    }

    public static void ApplyRecordingToggle(CheckBox button)
    {
        State state = States.GetValue(button, Attach);
        state.FollowChecked = true;
        Refresh(button, state);
    }

    private static State Attach(ButtonBase button)
    {
        var state = new State();
        button.EnabledChanged += (_, _) => Refresh(button, state);
        button.SystemColorsChanged += (_, _) => Refresh(button, state);
        if (button is CheckBox check)
            check.CheckedChanged += (_, _) => { if (state.FollowChecked) Refresh(button, state); };
        return state;
    }

    private static void Refresh(ButtonBase button, State state)
    {
        if (button.IsDisposed || button.Disposing) return;
        ButtonRole role = state.FollowChecked && button is CheckBox check
            ? check.Checked ? ButtonRole.Primary : ButtonRole.Neutral : state.Role;
        Palette palette = !button.Enabled ? Disabled : role switch
        {
            ButtonRole.Primary => Primary,
            ButtonRole.Secondary => Secondary,
            ButtonRole.Danger => Danger,
            ButtonRole.Pause => Pause,
            _ => Neutral
        };
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        if (SystemInformation.HighContrast)
        {
            bool emphasis = button.Enabled && role == ButtonRole.Primary;
            button.BackColor = emphasis ? SystemColors.Highlight : SystemColors.Control;
            button.ForeColor = !button.Enabled ? SystemColors.GrayText : emphasis ? SystemColors.HighlightText : SystemColors.ControlText;
            button.FlatAppearance.BorderColor = button.Enabled ? SystemColors.ControlText : SystemColors.GrayText;
            button.FlatAppearance.MouseOverBackColor = button.BackColor;
            button.FlatAppearance.MouseDownBackColor = button.BackColor;
        }
        else
        {
            button.BackColor = Rgb(palette.Background);
            button.ForeColor = Rgb(palette.Foreground);
            button.FlatAppearance.BorderColor = Rgb(palette.Border);
            button.FlatAppearance.MouseOverBackColor = Rgb(palette.Hover);
            button.FlatAppearance.MouseDownBackColor = Rgb(palette.Pressed);
        }
        // Flat button checkboxes otherwise replace the selected fill with the default system color.
        button.FlatAppearance.CheckedBackColor = button.BackColor;
    }

    private static Color Rgb(int value) => Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
}
