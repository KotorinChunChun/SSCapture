namespace SSCapture.Services;

internal readonly partial record struct HotkeyBinding
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
