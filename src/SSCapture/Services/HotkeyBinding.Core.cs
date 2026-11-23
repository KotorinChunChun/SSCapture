namespace SSCapture.Services;

[Flags]
internal enum HotkeyModifiers { None = 0, Windows = 1, Control = 2, Alt = 4, Shift = 8 }

internal readonly record struct HotkeyAssignment(HotkeyBinding Binding, CaptureIntent Intent, int DisplayNumber = 0, string? RegionId = null, string? PresetKey = null);

internal readonly partial record struct HotkeyBinding(int VirtualKey, HotkeyModifiers Modifiers)
{
    internal static HotkeyModifiers Normalize(KeyboardModifiers physical)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if ((physical & (KeyboardModifiers.LeftWindows | KeyboardModifiers.RightWindows)) != 0) result |= HotkeyModifiers.Windows;
        if ((physical & (KeyboardModifiers.LeftControl | KeyboardModifiers.RightControl | KeyboardModifiers.Control)) != 0) result |= HotkeyModifiers.Control;
        if ((physical & (KeyboardModifiers.LeftAlt | KeyboardModifiers.RightAlt | KeyboardModifiers.Alt)) != 0) result |= HotkeyModifiers.Alt;
        if ((physical & (KeyboardModifiers.LeftShift | KeyboardModifiers.RightShift | KeyboardModifiers.Shift)) != 0) result |= HotkeyModifiers.Shift;
        return result;
    }
}
