using System.Runtime.InteropServices;

namespace SSCapture.Services;

internal static partial class KeyDisplay
{
    internal static string Binding(string? value) => HotkeyBinding.Parse(value) is { } binding ? Binding(binding) : "なし";

    internal static string Binding(HotkeyBinding binding)
    {
        var labels = new List<string>(5);
        foreach (var (modifier, key) in new[]
        {
            (HotkeyModifiers.Windows, Keys.LWin), (HotkeyModifiers.Control, Keys.ControlKey),
            (HotkeyModifiers.Alt, Keys.Menu), (HotkeyModifiers.Shift, Keys.ShiftKey)
        })
            if (binding.Modifiers.HasFlag(modifier)) labels.Add(Key(key));
        labels.Add(Key((Keys)binding.VirtualKey));
        return string.Join("＋", labels);
    }

    internal static string Key(Keys key) => key switch
    {
        Keys.LWin or Keys.RWin => "Windows キー",
        Keys.ControlKey => "Ctrl",
        Keys.ShiftKey => "Shift",
        Keys.Menu => "Alt",
        Keys.Left => "←（左）",
        Keys.Up => "↑（上）",
        Keys.Right => "→（右）",
        Keys.Down => "↓（下）",
        Keys.PrintScreen => "Print Screen",
        Keys.Prior => "Page Up",
        Keys.Next => "Page Down",
        Keys.Back => "Backspace",
        Keys.Escape => "Esc",
        Keys.Return => "Enter",
        Keys.Capital => "Caps Lock",
        Keys.Scroll => "Scroll Lock",
        Keys.NumLock => "Num Lock",
        Keys.IMEConvert => "変換",
        Keys.IMENonconvert => "無変換",
        Keys.KanaMode => "カタカナ／ひらがな",
        >= Keys.D0 and <= Keys.D9 => ((char)key).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"テンキー {key - Keys.NumPad0}",
        Keys.Multiply => "テンキー ×",
        Keys.Divide => "テンキー ÷",
        Keys.Subtract => "テンキー −",
        Keys.Add => "テンキー ＋",
        Keys.Decimal => "テンキー .",
        >= Keys.Oem1 and <= Keys.Oem3 or >= Keys.Oem4 and <= Keys.Oem8 or Keys.Oem102 => LayoutKey(key),
        _ => key.ToString()
    };

    private static string LayoutKey(Keys key)
    {
        nint foreground = GetForegroundWindow();
        uint thread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, 0);
        nint layout = GetKeyboardLayout(thread);
        uint character = MapVirtualKeyExW((uint)key, 2, layout) & 0xffff;
        return character >= 32 ? ((char)character).ToString() : key.ToString();
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, nint process);
    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint thread);
    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);
}
