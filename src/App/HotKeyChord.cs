using static Overlyric.Interop.NativeMethods;

namespace Overlyric.App;

/// <summary>
/// A key combination: Win32 modifier flags plus a virtual-key code.
/// <para>
/// The text form ("Ctrl+Alt+H") is what lives in <c>config.json</c>, so the file stays readable and
/// hand-editable; parsing never throws, and anything unrecognised degrades to "no shortcut" rather
/// than losing the rest of the configuration.
/// </para>
/// </summary>
internal readonly record struct HotKeyChord(uint Modifiers, uint VirtualKey)
{
    public static HotKeyChord None => default;

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>A shortcut needs a real modifier: Shift alone is not a global hotkey.</summary>
    public bool HasStrongModifier => (Modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) != 0;

    public static HotKeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var modifiers = 0u;
        var key = 0u;

        foreach (var token in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    continue;

                case "alt":
                    modifiers |= MOD_ALT;
                    continue;

                case "shift":
                    modifiers |= MOD_SHIFT;
                    continue;

                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    continue;
            }

            if (key != 0)
            {
                // Two non-modifier tokens: the text is not a chord.
                return None;
            }

            key = KeyFromName(token.Trim());

            if (key == 0)
            {
                return None;
            }
        }

        return key == 0 ? None : new HotKeyChord(modifiers, key);
    }

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);

        if ((Modifiers & MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & MOD_WIN) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x20] = "Space",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x1B] = "Esc",
        [0x08] = "Backspace",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x24] = "Home",
        [0x23] = "End",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0xBC] = "Comma",
        [0xBE] = "Period",
        [0xBD] = "Minus",
        [0xBB] = "Plus",
        [0xBF] = "Slash",
        [0xBA] = "Semicolon",
        [0xDE] = "Quote",
        [0xDC] = "Backslash",
        [0xDB] = "BracketLeft",
        [0xDD] = "BracketRight",
        [0xC0] = "Tilde"
    };

    /// <summary>The name a key is shown and stored under: "H", "0", "F5", "Comma".</summary>
    public static string KeyName(uint virtualKey)
    {
        if (Names.TryGetValue(virtualKey, out var named))
        {
            return named;
        }

        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        return $"Key{virtualKey}";
    }

    private static uint KeyFromName(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);

            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }

            return c switch
            {
                ',' => 0xBC,
                '.' => 0xBE,
                '-' => 0xBD,
                '+' => 0xBB,
                '/' => 0xBF,
                ';' => 0xBA,
                '\'' => 0xDE,
                '\\' => 0xDC,
                '[' => 0xDB,
                ']' => 0xDD,
                '`' => 0xC0,
                _ => 0
            };
        }

        foreach (var (vk, named) in Names)
        {
            if (string.Equals(named, name, StringComparison.OrdinalIgnoreCase))
            {
                return vk;
            }
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var number)
            && number is >= 1 and <= 24)
        {
            return (uint)(0x6F + number);
        }

        if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(name[3..], out var raw))
        {
            return raw;
        }

        return 0;
    }
}
