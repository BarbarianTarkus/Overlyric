using Overlyric.App;
using static Overlyric.Interop.NativeMethods;

namespace Overlyric.ShortcutsTests;

/// <summary>
/// Checks the text form of a hotkey, which is the part of the shortcut editor that a user can break
/// by hand: <c>config.json</c> is editable, so every string in it has to survive parsing or fail
/// cleanly to "unbound" without taking the rest of the settings with it.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        // ---- Parsing the stored form ----
        var h = HotKeyChord.Parse("Ctrl+Alt+H");
        Check("Ctrl+Alt+H parses to Ctrl|Alt", h.Modifiers == (MOD_CONTROL | MOD_ALT), h);
        Check("Ctrl+Alt+H keeps the virtual key", h.VirtualKey == 0x48, h);
        Check("Ctrl+Alt+H round-trips", h.ToString() == "Ctrl+Alt+H", h.ToString());

        var lower = HotKeyChord.Parse("ctrl+alt+h");
        Check("lower case is accepted", lower == h, lower);

        var reordered = HotKeyChord.Parse("Alt+Ctrl+H");
        Check("modifier order is canonicalised", reordered.ToString() == "Ctrl+Alt+H", reordered.ToString());

        var f5 = HotKeyChord.Parse("Ctrl+Shift+F5");
        Check("F5 maps to 0x74", f5.VirtualKey == 0x74, f5);
        Check("F5 round-trips", f5.ToString() == "Ctrl+Shift+F5", f5.ToString());

        var win = HotKeyChord.Parse("Win+Space");
        Check("Win+Space parses", win.Modifiers == MOD_WIN && win.VirtualKey == 0x20, win);
        Check("Win+Space round-trips", win.ToString() == "Win+Space", win.ToString());

        // ---- Punctuation: stored by name, accepted literally ----
        Check("Comma maps to VK_OEM_COMMA", HotKeyChord.Parse("Ctrl+Alt+Comma").VirtualKey == 0xBC, "");
        Check("a literal comma parses the same", HotKeyChord.Parse("Ctrl+Alt+,") == HotKeyChord.Parse("Ctrl+Alt+Comma"), "");
        Check("Period maps to VK_OEM_PERIOD", HotKeyChord.Parse("Ctrl+Alt+Period").VirtualKey == 0xBE, "");
        Check("a literal period parses the same", HotKeyChord.Parse("Ctrl+Alt+.") == HotKeyChord.Parse("Ctrl+Alt+Period"), "");
        Check(
            "punctuation is stored by name, not as a bare character",
            HotKeyChord.Parse("Ctrl+Alt+,").ToString() == "Ctrl+Alt+Comma",
            HotKeyChord.Parse("Ctrl+Alt+,").ToString());

        Check("digit zero is a key, not a modifier", HotKeyChord.Parse("Ctrl+Alt+0").VirtualKey == 0x30, "");

        // ---- Refusals: every one of these must come back empty, never throw ----
        Check("empty text is unbound", HotKeyChord.Parse("").IsEmpty, "");
        Check("null is unbound", HotKeyChord.Parse(null).IsEmpty, "");
        Check("whitespace is unbound", HotKeyChord.Parse("   ").IsEmpty, "");
        Check("modifiers alone are not a chord", HotKeyChord.Parse("Ctrl+Alt").IsEmpty, "");
        Check("two keys are not a chord", HotKeyChord.Parse("Ctrl+Alt+H+J").IsEmpty, "");
        Check("an unknown key name is refused", HotKeyChord.Parse("Ctrl+Bogus").IsEmpty, "");
        Check("garbage is refused", HotKeyChord.Parse("!!!").IsEmpty, "");

        // ---- The rule that keeps a bare key from becoming a global hotkey ----
        Check("Shift+A is not a strong modifier", !HotKeyChord.Parse("Shift+A").HasStrongModifier, "");
        Check("Ctrl+A is", HotKeyChord.Parse("Ctrl+A").HasStrongModifier, "");
        Check("Alt+A is", HotKeyChord.Parse("Alt+A").HasStrongModifier, "");
        Check("Win+A is", HotKeyChord.Parse("Win+A").HasStrongModifier, "");

        // ---- Round-trip every chord the app ships as a default ----
        string[] defaults =
        [
            "Ctrl+Alt+H", "Ctrl+Alt+M", "Ctrl+Alt+L", "Ctrl+Alt+Comma",
            "Ctrl+Alt+Period", "Ctrl+Alt+0", "Ctrl+Alt+Shift+Left", "Ctrl+Win+PageUp"
        ];

        foreach (var text in defaults)
        {
            var parsed = HotKeyChord.Parse(text);
            var again = HotKeyChord.Parse(parsed.ToString());
            Check($"'{text}' survives a format/parse round trip", parsed == again, parsed.ToString());
        }

        // ---- Unknown keys still survive a round trip instead of vanishing ----
        var exotic = new HotKeyChord(MOD_CONTROL, 0xE2);
        Check("an unnamed key still round-trips", HotKeyChord.Parse(exotic.ToString()) == exotic, exotic.ToString());

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "ALL CHECKS PASSED"
            : $"{_failures} CHECK(S) FAILED");

        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, object detail)
    {
        if (ok)
        {
            Console.WriteLine($"PASS | {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL | {what}   (got: {detail})");
    }
}
