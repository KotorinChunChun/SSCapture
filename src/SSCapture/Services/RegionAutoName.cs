using System.Globalization;

namespace SSCapture.Services;

internal static class RegionAutoName
{
    internal static string Create(string shortcut, Rectangle bounds)
    {
        string prefix = "";
        if (HotkeyBinding.Parse(shortcut) is { } binding)
        {
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) prefix += "W";
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) prefix += "C";
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) prefix += "A";
            if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) prefix += "S";
            prefix += new HotkeyBinding(binding.VirtualKey, HotkeyModifiers.None).ToString().ToUpperInvariant() + "_";
        }
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}X{bounds.X}_Y{bounds.Y}_W{bounds.Width}_H{bounds.Height}");
    }
}
