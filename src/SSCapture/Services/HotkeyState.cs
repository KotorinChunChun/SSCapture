namespace SSCapture.Services;

[Flags]
internal enum KeyboardModifiers
{
    None = 0,
    LeftWindows = 1, RightWindows = 2,
    LeftShift = 4, RightShift = 8, Shift = 16,
    LeftControl = 32, RightControl = 64, Control = 128,
    LeftAlt = 256, RightAlt = 512, Alt = 1024
}

internal readonly record struct HotkeyDecision(bool Suppress, bool Capture, CaptureIntent Intent = CaptureIntent.Screenshot, int DisplayNumber = 0, string? RegionId = null);

// One state machine per hook. No native input or UI dependencies.
internal sealed class HotkeyState
{
    private readonly Dictionary<int, HotkeyAssignment[]> bindings;
    private readonly bool[] down = new bool[256];
    private readonly HotkeyAssignment?[] suppressedIntent = new HotkeyAssignment?[256];
    private KeyboardModifiers modifiers;

    // Preserve the original state-machine entry point and its Win+Shift+S behavior.
    internal HotkeyState(KeyboardModifiers initialModifiers = KeyboardModifiers.None, bool sInitiallyDown = false)
        : this([new(HotkeyBinding.Parse("Win+Shift+S")!.Value, CaptureIntent.Screenshot)],
            initialModifiers, sInitiallyDown ? [0x53] : []) { }

    internal HotkeyState(IReadOnlyList<HotkeyAssignment> assignments,
        KeyboardModifiers initialModifiers = KeyboardModifiers.None, IEnumerable<int>? initiallyDown = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        bindings = assignments.GroupBy(item => item.Binding.VirtualKey).ToDictionary(group => group.Key, group => group.ToArray());
        modifiers = initialModifiers;
        if (initiallyDown is not null)
            foreach (int key in initiallyDown)
                if (IsTargetKey(key)) down[key] = true;
    }

    internal void SynchronizeModifiers(KeyboardModifiers current) => modifiers = current;
    internal bool IsTargetKey(int virtualKey) => bindings.ContainsKey(virtualKey);

    internal HotkeyDecision Process(int virtualKey, bool isDown, bool enabled)
    {
        KeyboardModifiers modifier = virtualKey switch
        {
            0x5B => KeyboardModifiers.LeftWindows, 0x5C => KeyboardModifiers.RightWindows,
            0xA0 => KeyboardModifiers.LeftShift, 0xA1 => KeyboardModifiers.RightShift, 0x10 => KeyboardModifiers.Shift,
            0xA2 => KeyboardModifiers.LeftControl, 0xA3 => KeyboardModifiers.RightControl, 0x11 => KeyboardModifiers.Control,
            0xA4 => KeyboardModifiers.LeftAlt, 0xA5 => KeyboardModifiers.RightAlt, 0x12 => KeyboardModifiers.Alt,
            _ => KeyboardModifiers.None
        };
        if (modifier != KeyboardModifiers.None)
        {
            if (isDown) modifiers |= modifier;
            else modifiers &= ~modifier;
            return default;
        }
        if (!bindings.TryGetValue(virtualKey, out HotkeyAssignment[]? candidates)) return default;
        if (!isDown)
        {
            bool wasDown = down[virtualKey];
            HotkeyAssignment? suppressed = suppressedIntent[virtualKey];
            down[virtualKey] = false;
            suppressedIntent[virtualKey] = null;
            if (suppressed is HotkeyAssignment intent) return new(true, false, intent.Intent, intent.DisplayNumber, intent.RegionId);
            // Some PrintScreen paths deliver only a release. A matched down/up pair
            // has already been handled above; never turn an unmatched chord's known
            // release into a bare PrintScreen action when its modifiers were released.
            if (virtualKey == 0x2C && !wasDown && enabled)
            {
                HotkeyAssignment? releaseIntent = Match(candidates);
                if (releaseIntent is HotkeyAssignment release) return new(true, true, release.Intent, release.DisplayNumber, release.RegionId);
            }
            return default;
        }
        if (down[virtualKey])
            return suppressedIntent[virtualKey] is HotkeyAssignment repeated ? new(true, false, repeated.Intent, repeated.DisplayNumber, repeated.RegionId) : default;
        down[virtualKey] = true;
        HotkeyAssignment? matched = enabled ? Match(candidates) : null;
        suppressedIntent[virtualKey] = matched;
        return matched is HotkeyAssignment capture ? new(true, true, capture.Intent, capture.DisplayNumber, capture.RegionId) : default;
    }

    private HotkeyAssignment? Match(HotkeyAssignment[] candidates)
    {
        HotkeyModifiers current = HotkeyBinding.Normalize(modifiers);
        foreach (HotkeyAssignment candidate in candidates)
            if (candidate.Binding.Modifiers == current) return candidate;
        return null;
    }
}
