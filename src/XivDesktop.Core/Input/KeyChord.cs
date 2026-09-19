namespace XivDesktop.Core.Input;

[Flags]
public enum KeyMods
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Super = 8,
}

/// <summary>
/// A key combination such as <c>Super+Shift+1</c>: modifiers plus one Windows virtual-key code. Parsing is
/// case-insensitive and accepts common aliases (Win/Mod4/Meta for Super, Control, Return, Esc…); formatting
/// is canonical (<c>Super+Ctrl+Alt+Shift+Key</c>), so a parsed chord round-trips through the config.
/// </summary>
public readonly record struct KeyChord(KeyMods Mods, int Key)
{
    // Windows virtual-key codes for the modifiers (the game and Wine report these).
    public const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    public const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    public static readonly KeyChord None = default;

    public bool IsNone => Key == 0;

    private static readonly (string Name, int Vk)[] Named =
    [
        ("Enter", 0x0D), ("Space", 0x20), ("Tab", 0x09), ("Escape", 0x1B), ("Backspace", 0x08), ("Delete", 0x2E),
        ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Minus", 0xBD), ("Equals", 0xBB), ("Comma", 0xBC), ("Period", 0xBE), ("Slash", 0xBF), ("Semicolon", 0xBA),
        ("Grave", 0xC0), ("LeftBracket", 0xDB), ("RightBracket", 0xDD), ("Backslash", 0xDC), ("Quote", 0xDE),
    ];

    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["return"] = 0x0D, ["ret"] = 0x0D, ["esc"] = 0x1B, ["del"] = 0x2E, ["ins"] = 0x2D, ["pgup"] = 0x21, ["pgdn"] = 0x22,
        ["pagedn"] = 0x22, ["-"] = 0xBD, ["="] = 0xBB, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, [";"] = 0xBA, ["`"] = 0xC0,
        ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["'"] = 0xDE, ["bksp"] = 0x08,
    };

    public static bool IsModifierKey(int vk) => vk is VkShift or VkControl or VkMenu or VkLWin or VkRWin
        or VkLShift or VkRShift or VkLControl or VkRControl or VkLMenu or VkRMenu;

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var mods = KeyMods.None;
        var key = 0;

        // '+' separates parts, so the plus key itself is spelled "Equals" (its unshifted key); "Super++" is refused.
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0)
                return false;
            var mod = ParseMod(p);
            if (mod != KeyMods.None)
            {
                if ((mods & mod) != 0)
                    return false; // "Shift+Shift+A"
                mods |= mod;
                continue;
            }

            if (key != 0 || i != parts.Length - 1)
                return false; // one key, and it comes last
            key = ParseKey(p);
            if (key == 0)
                return false;
        }

        if (key == 0)
            return false;
        chord = new KeyChord(mods, key);
        return true;
    }

    public static KeyChord Parse(string text) => TryParse(text, out var c) ? c : throw new FormatException($"not a key chord: \"{text}\"");

    private static KeyMods ParseMod(string p) => p.ToLowerInvariant() switch
    {
        "super" or "win" or "windows" or "mod4" or "meta" or "cmd" or "logo" => KeyMods.Super,
        "ctrl" or "control" or "ctl" => KeyMods.Ctrl,
        "alt" or "mod1" or "option" => KeyMods.Alt,
        "shift" => KeyMods.Shift,
        _ => KeyMods.None,
    };

    public static int ParseKey(string p)
    {
        if (p.Length == 1)
        {
            var c = char.ToUpperInvariant(p[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return c;
        }

        foreach (var (name, vk) in Named)
        {
            if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase))
                return vk;
        }

        if (Aliases.TryGetValue(p, out var alias))
            return alias;
        if ((p[0] is 'F' or 'f') && int.TryParse(p.AsSpan(1), out var f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        if (p.StartsWith("num", StringComparison.OrdinalIgnoreCase) && p.Length == 4 && char.IsDigit(p[3]))
            return 0x60 + (p[3] - '0');
        return 0;
    }

    public static string KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return ((char)vk).ToString();
        foreach (var (name, v) in Named)
        {
            if (v == vk)
                return name;
        }

        if (vk is >= 0x70 and <= 0x87)
            return "F" + (vk - 0x70 + 1);
        if (vk is >= 0x60 and <= 0x69)
            return "Num" + (vk - 0x60);
        return $"VK{vk:X2}";
    }

    public override string ToString()
    {
        if (IsNone)
            return "";
        var s = "";
        if ((Mods & KeyMods.Super) != 0)
            s += "Super+";
        if ((Mods & KeyMods.Ctrl) != 0)
            s += "Ctrl+";
        if ((Mods & KeyMods.Alt) != 0)
            s += "Alt+";
        if ((Mods & KeyMods.Shift) != 0)
            s += "Shift+";
        return s + KeyName(Key);
    }

    /// <summary>The modifiers currently held, from a key-down query over virtual-key codes.</summary>
    public static KeyMods ModsFrom(Func<int, bool> isDown)
    {
        var m = KeyMods.None;
        if (isDown(VkShift) || isDown(VkLShift) || isDown(VkRShift))
            m |= KeyMods.Shift;
        if (isDown(VkControl) || isDown(VkLControl) || isDown(VkRControl))
            m |= KeyMods.Ctrl;
        if (isDown(VkMenu) || isDown(VkLMenu) || isDown(VkRMenu))
            m |= KeyMods.Alt;
        if (isDown(VkLWin) || isDown(VkRWin))
            m |= KeyMods.Super;
        return m;
    }

    /// <summary>Held exactly: the key is down and the held modifiers equal the chord's (Super+1 does not fire on Super+Shift+1).</summary>
    public bool IsHeld(Func<int, bool> isDown) => !IsNone && isDown(Key) && ModsFrom(isDown) == Mods;
}
