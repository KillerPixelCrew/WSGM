using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSGM.Core;

/// <summary>The keys a hotkey can send, with the names and groups the Overlay's key picker shows.</summary>
/// <remarks>
///     Values are Windows virtual keys. Punctuation is named by its US-layout symbol; another layout sends the same
///     physical key. Numpad Enter uses Handheld Companion's <c>0x4000000D</c>.
/// </remarks>
public static class HotkeyKeys
{
    /// <summary>Ctrl.</summary>
    public const int Control = 0x11;

    /// <summary>Alt.</summary>
    public const int Alt = 0x12;

    /// <summary>Shift.</summary>
    public const int Shift = 0x10;

    /// <summary>The left Windows key.</summary>
    public const int Windows = 0x5B;

    /// <summary>Insert, OptiScaler's default menu key.</summary>
    public const int Insert = 0x2D;

    /// <summary>The S key, part of Lossless Scaling's default scale hotkey.</summary>
    public const int S = 0x53;

    /// <summary>Numpad Enter, as Handheld Companion stores it.</summary>
    public const int NumpadEnter = 0x4000000D;

    /// <summary>The four modifier switches, in the order a chord presses them.</summary>
    public static readonly IReadOnlyList<int> Modifiers = [Control, Alt, Shift, Windows];

    /// <summary>The picker's groups and their keys, in display order.</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<(int Key, string Label)> Keys)> Groups =
    [
        ("Navigation", [
            (Insert, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "PgUp"), (0x22, "PgDn"),
            (0x26, "Up"), (0x28, "Down"), (0x25, "Left"), (0x27, "Right")
        ]),
        ("Function keys", [.. Enumerable.Range(1, 24).Select(index => (0x6F + index, "F" + index))]),
        ("Letters", [.. Enumerable.Range('A', 26).Select(letter => (letter, ((char)letter).ToString()))]),
        ("Numbers", [.. Enumerable.Range('0', 10).Select(digit => (digit, ((char)digit).ToString()))]),
        ("Other keys", [
            (0x20, "Space"), (0x0D, "Enter"), (0x1B, "Esc"), (0x09, "Tab"), (0x08, "Backspace"),
            (0x2C, "Print Screen"), (0x13, "Pause")
        ]),
        ("Numpad", [
            .. Enumerable.Range(0, 10).Select(digit => (0x60 + digit, "Num " + digit)),
            (0x6F, "Num /"), (0x6A, "Num *"), (0x6D, "Num -"), (0x6B, "Num +"), (0x6E, "Num ."),
            (NumpadEnter, "Num Enter"), (0x90, "Num Lock")
        ]),
        ("Punctuation", [
            (0xBD, "-"), (0xBB, "="), (0xDB, "["), (0xDD, "]"), (0xDC, "\\"), (0xBA, ";"), (0xDE, "'"),
            (0xBC, ","), (0xBE, "."), (0xBF, "/"), (0xC0, "`")
        ])
    ];

    private static readonly Dictionary<int, string> Labels = Groups.SelectMany(group => group.Keys)
        .Concat<(int Key, string Label)>([(Control, "Ctrl"), (Alt, "Alt"), (Shift, "Shift"), (Windows, "Win")])
        .ToDictionary(key => key.Key, key => key.Label);

    /// <summary>Whether a stored value is a key Windows can send: a virtual key from 1 to 254, or Numpad Enter.</summary>
    /// <param name="key">The stored value.</param>
    /// <returns>True for a sendable key.</returns>
    public static bool IsValid(int key)
    {
        return key is > 0 and < 0xFF or NumpadEnter;
    }

    /// <summary>Whether a key is one of the four modifier switches.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True for Ctrl, Alt, Shift and Win.</returns>
    public static bool IsModifier(int key)
    {
        return Modifiers.Contains(key);
    }

    /// <summary>A key's keycap text.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its name, or its virtual-key number for a key outside the picker.</returns>
    public static string Label(int key)
    {
        return Labels.TryGetValue(key, out var label)
            ? label
            : "Key " + (key & 0xFFFF).ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>A chord as text, such as <c>Ctrl+Alt+S</c>.</summary>
    /// <param name="keys">The keys in press order.</param>
    /// <returns>The text, or <c>No keys</c>.</returns>
    public static string Describe(IReadOnlyList<int> keys)
    {
        return keys.Count == 0 ? "No keys" : string.Join("+", keys.Select(Label));
    }

    /// <summary>Turns a modifier on or off, keeping the modifiers in switch order ahead of the other keys.</summary>
    /// <param name="keys">The chord.</param>
    /// <param name="modifier">One of <see cref="Modifiers" />.</param>
    /// <param name="on">Whether it is pressed.</param>
    /// <returns>The new chord.</returns>
    public static List<int> WithModifier(IReadOnlyList<int> keys, int modifier, bool on)
    {
        var modifiers = Modifiers.Where(candidate =>
            candidate == modifier ? on : keys.Contains(candidate));
        return [.. modifiers, .. keys.Where(key => !IsModifier(key))];
    }

    /// <summary>Replaces the chord's other keys with one key, keeping its modifiers.</summary>
    /// <param name="keys">The chord.</param>
    /// <param name="key">The key to press with the modifiers.</param>
    /// <returns>The new chord.</returns>
    public static List<int> WithKey(IReadOnlyList<int> keys, int key)
    {
        return [.. keys.Where(IsModifier), key];
    }
}
