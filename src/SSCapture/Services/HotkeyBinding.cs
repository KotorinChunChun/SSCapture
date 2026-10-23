namespace SSCapture.Services;

[Flags]
internal enum HotkeyModifiers { None = 0, Windows = 1, Control = 2, Alt = 4, Shift = 8 }

internal readonly record struct HotkeyAssignment(HotkeyBinding Binding, CaptureIntent Intent, int DisplayNumber = 0, string? RegionId = null);

internal readonly record struct HotkeyBinding(int VirtualKey, HotkeyModifiers Modifiers)
{
    /// <summary>Blank/null means unassigned. Key names are case-insensitive.</summary>
    public static HotkeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (string part in text.Split('+'))
        {
            string token = part.Trim();
            if (token.Length == 0) throw new ArgumentException("ショートカットの + の前後にキーを指定してください。");
            HotkeyModifiers modifier = token.ToUpperInvariant() switch
            {
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None
            };
            if (modifier != HotkeyModifiers.None)
            {
                if ((modifiers & modifier) != 0) throw new ArgumentException("ショートカットの修飾キーが重複しています。");
                modifiers |= modifier;
                continue;
            }
            if (key is not null) throw new ArgumentException("ショートカットには通常のキーを1つだけ指定してください。");
            key = ParseKey(token);
        }
        if (key is null) throw new ArgumentException("修飾キーだけのショートカットは使用できません。");
        return new HotkeyBinding(key.Value, modifiers);
    }

    private static int ParseKey(string token)
    {
        string name = token.ToUpperInvariant() switch
        {
            "PRINTSCREEN" or "PRINTSCRN" or "PRTSC" or "PRTSCN" or "SNAPSHOT" => "PrintScreen",
            "ESC" => "Escape",
            "BACKSPACE" => "Back",
            "PAGEUP" or "PGUP" => "PageUp",
            "PAGEDOWN" or "PGDN" => "PageDown",
            "DEL" => "Delete",
            "INS" => "Insert",
            "PLUS" => "Oemplus",
            "MINUS" => "OemMinus",
            _ when token.Length == 1 && token[0] is >= '0' and <= '9' => $"D{token}",
            _ => token
        };
        // Enum.TryParse accepts arbitrary numeric values and comma-separated bitwise
        // combinations; neither is a valid user-facing key name.
        if (name.Contains(',') || !char.IsLetter(name[0]) ||
            !Enum.TryParse(name, true, out Keys parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"ショートカットのキー「{token}」は認識できません。");
        int key = (int)parsed;
        if (key == 0x1B) throw new ArgumentException("Escapeは撮影キャンセルに使用するため、ショートカットに割り当てられません。");
        if (key is < 0x08 or > 0xFE or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5 or 0xE5 or 0xE7)
            throw new ArgumentException("マウスボタン・修飾キー・入力処理用キーは通常のキーとして指定できません。");
        return key;
    }

    public static void ValidateSettings(AppSettings settings) => _ = ReadAssignments(settings);

    internal static HotkeyAssignment[] ReadAssignments(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.InstantShortcuts is null || settings.InstantShortcuts.Length != 10)
            throw new ArgumentException("即時撮影のショートカットはディスプレイ1～10の10件を指定してください。");
        if (settings.SavedRegions is null || settings.SavedRegions.Any(region => region is null)) throw new ArgumentException("登録範囲が不正です。");
        List<(string Label, string? Text, CaptureIntent Intent, int DisplayNumber, string? RegionId)> slots =
        [
            ("静止画のキー1", settings.ScreenshotShortcut1, CaptureIntent.Screenshot, 0, null),
            ("静止画のキー2", settings.ScreenshotShortcut2, CaptureIntent.Screenshot, 0, null),
            ("録画のキー1", settings.RecordingShortcut1, CaptureIntent.Recording, 0, null),
            ("録画のキー2", settings.RecordingShortcut2, CaptureIntent.Recording, 0, null),
            ("前回範囲", settings.LastRegionShortcut, CaptureIntent.Screenshot, 0, AppSettings.LastRegionKeyId),
            ("直前と同じ設定で再撮影", settings.LastRecipeShortcut, CaptureIntent.Screenshot, 0, AppSettings.LastRecipeKeyId)
        ];
        slots.AddRange(settings.InstantShortcuts.Select((text, index) => ($"即時撮影 {index + 1}", (string?)text, CaptureIntent.Screenshot, index + 1, (string?)null)));
        slots.AddRange(settings.SavedRegions.Select(region => ($"登録範囲「{region.Name}」", (string?)region.Shortcut, CaptureIntent.Screenshot, 0, (string?)region.Id)));
        var assignments = new List<HotkeyAssignment>(14);
        var used = new Dictionary<HotkeyBinding, string>();
        foreach (var (label, text, intent, displayNumber, regionId) in slots)
        {
            HotkeyBinding? parsed;
            try { parsed = Parse(text); }
            catch (ArgumentException ex) { throw new ArgumentException($"{label}: {ex.Message}", ex); }
            if (parsed is not HotkeyBinding binding) continue;
            if (used.TryGetValue(binding, out string? first))
                throw new ArgumentException($"{label}と{first}のショートカットが重複しています（{binding}）。");
            used.Add(binding, label);
            assignments.Add(new HotkeyAssignment(binding, intent, displayNumber, regionId));
        }
        return assignments.ToArray();
    }

    internal static HotkeyModifiers Normalize(KeyboardModifiers physical)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if ((physical & (KeyboardModifiers.LeftWindows | KeyboardModifiers.RightWindows)) != 0) result |= HotkeyModifiers.Windows;
        if ((physical & (KeyboardModifiers.LeftControl | KeyboardModifiers.RightControl | KeyboardModifiers.Control)) != 0) result |= HotkeyModifiers.Control;
        if ((physical & (KeyboardModifiers.LeftAlt | KeyboardModifiers.RightAlt | KeyboardModifiers.Alt)) != 0) result |= HotkeyModifiers.Alt;
        if ((physical & (KeyboardModifiers.LeftShift | KeyboardModifiers.RightShift | KeyboardModifiers.Shift)) != 0) result |= HotkeyModifiers.Shift;
        return result;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & HotkeyModifiers.Windows) != 0) parts.Add("Win");
        if ((Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        parts.Add(VirtualKey switch
        {
            0x2C => "PrintScreen",
            >= 0x30 and <= 0x39 => ((char)VirtualKey).ToString(),
            _ => ((Keys)VirtualKey).ToString()
        });
        return string.Join('+', parts);
    }
}
