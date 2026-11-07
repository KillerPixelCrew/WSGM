using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WSGM.Core;

/// <summary>Validated native hotkey formats, with one Windows-key representation for both editors.</summary>
internal static class GameToolHotkeys
{
    internal static readonly (int Key, string Label)[] Keys = [.. HotkeyKeys.Groups.SelectMany(group => group.Keys)
        .Where(key => key.Key < 255 && !HotkeyKeys.IsModifier(key.Key))];

    internal static bool TryParse(string value, string format, out int[] keys)
    {
        keys = [];
        if (format == "vk-array")
        {
            var parts = value.Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], out var key) || key is < 0 or > 254
                || parts.Skip(1).Any(part => part is not ("0" or "1")))
            {
                return false;
            }

            keys = [.. parts[1] == "1" ? new[] { HotkeyKeys.Control } : Array.Empty<int>(),
                .. parts[2] == "1" ? new[] { HotkeyKeys.Shift } : Array.Empty<int>(),
                .. parts[3] == "1" ? new[] { HotkeyKeys.Alt } : Array.Empty<int>(), .. key == 0 ? Array.Empty<int>() : new[] { key }];
            return key == 0 || !HotkeyKeys.IsModifier(key);
        }

        if (value.Length == 0 || value == "Not Bound")
        {
            return true;
        }

        List<int> parsed = [];
        foreach (var part in value.Split('+'))
        {
            var key = part.Trim() switch { "Ctrl" => HotkeyKeys.Control, "Shift" => HotkeyKeys.Shift,
                "Alt" => HotkeyKeys.Alt, _ => Keys.FirstOrDefault(key => key.Label.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase)).Key };
            if (key == 0 || parsed.Contains(key))
            {
                return false;
            }

            parsed.Add(key);
        }

        keys = [.. parsed];
        return parsed.Count(key => !HotkeyKeys.IsModifier(key)) == 1;
    }

    internal static string Format(IReadOnlyList<int> keys, string format)
    {
        var main = keys.FirstOrDefault(key => !HotkeyKeys.IsModifier(key));
        if (keys.Contains(HotkeyKeys.Windows) || keys.Count(key => !HotkeyKeys.IsModifier(key)) > 1)
        {
            throw new GameToolException("Use one key with Ctrl, Shift or Alt.");
        }
        if (format == "vk-array")
        {
            if (main == 0)
            {
                return "0,0,0,0";
            }
            return string.Join(',', main.ToString(CultureInfo.InvariantCulture), keys.Contains(HotkeyKeys.Control) ? "1" : "0",
                keys.Contains(HotkeyKeys.Shift) ? "1" : "0", keys.Contains(HotkeyKeys.Alt) ? "1" : "0");
        }

        return main == 0 ? "Not Bound" : string.Join('+', new[]
        {
            keys.Contains(HotkeyKeys.Control) ? "Ctrl" : "", keys.Contains(HotkeyKeys.Shift) ? "Shift" : "",
            keys.Contains(HotkeyKeys.Alt) ? "Alt" : "", Keys.FirstOrDefault(key => key.Key == main).Label ?? ""
        }.Where(value => value.Length > 0));
    }

    internal static string Label(string value, string format) => TryParse(value, format, out var keys)
        ? keys.Length == 0 ? "Not bound" : Format(keys, "named") : "Custom";
}
