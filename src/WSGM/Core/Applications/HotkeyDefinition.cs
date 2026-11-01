using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     A key combination the Overlay sends to the game in front, such as Insert for OptiScaler's menu. Not the
///     shortcut that opens the Overlay, which is <see cref="HotkeyConfig" />.
/// </summary>
public sealed record HotkeyDefinition
{
    /// <summary>The longest press delay, in milliseconds; Handheld Companion clamps its keystrokes the same way.</summary>
    public const int MaximumPressDelayMs = 5000;

    /// <summary>Stable identity, also used by the Quick access pin <c>hotkey:{Id}</c>.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing name.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Windows virtual keys in press order. Numpad Enter is <c>0x4000000D</c>, Enter with the extended flag, as
    ///     Handheld Companion stores it.
    /// </summary>
    public List<int> Keys { get; set; } = [];

    /// <summary>How long every key stays down before release, 0 to 5000 ms.</summary>
    public int PressDelayMs { get; set; }

    /// <summary>A copy that shares no list with this definition.</summary>
    /// <returns>The copy.</returns>
    public HotkeyDefinition Copy()
    {
        return this with { Keys = [.. Keys] };
    }

    /// <summary>
    ///     Keeps every entry that names at least one valid key: invalid key codes are dropped, an entry left
    ///     without keys is dropped, a missing or repeated id gets a new one and the press delay is clamped.
    /// </summary>
    /// <param name="entries">The stored entries.</param>
    /// <returns>The normalized list.</returns>
    internal static List<HotkeyDefinition> Normalize(List<HotkeyDefinition>? entries)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<HotkeyDefinition> result = [];
        foreach (var entry in entries ?? [])
        {
            if (entry is null)
            {
                continue;
            }

            entry.Keys = (entry.Keys ?? []).Where(HotkeyKeys.IsValid).Distinct().ToList();
            if (entry.Keys.Count == 0)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
                ids.Add(entry.Id);
            }

            entry.Name = string.IsNullOrWhiteSpace(entry.Name) ? "Hotkey" : entry.Name.Trim();
            entry.PressDelayMs = Math.Clamp(entry.PressDelayMs, 0, MaximumPressDelayMs);
            result.Add(entry);
        }

        return result;
    }
}
