using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Persistent widget identity, independent of runtime generation and display order.</summary>
/// <param name="PluginId">Owning plugin identity.</param>
/// <param name="InstanceId">Configured plugin instance.</param>
/// <param name="WidgetId">Stable widget declaration identity.</param>
public sealed record PluginWidgetPin(string PluginId, string InstanceId, string WidgetId);

/// <summary>Bounded pin ordering that retains unavailable plugin identities.</summary>
internal static class PluginWidgetPins
{
    internal static List<PluginWidgetPin> Normalize(IEnumerable<PluginWidgetPin>? pins) =>
        (pins ?? []).Where(pin => pin is not null && Valid(pin.PluginId) && Valid(pin.InstanceId) && Valid(pin.WidgetId))
            .Distinct().Take(64).ToList();

    internal static void Set(List<PluginWidgetPin> pins, PluginWidgetPin pin, bool pinned)
    {
        if (Normalize([pin]).Count == 0) { throw new ArgumentException("Invalid widget identity.", nameof(pin)); }
        if (!pinned) { pins.RemoveAll(item => item == pin); return; }
        if (pins.Contains(pin)) { return; }
        if (pins.Count >= 64) { throw new InvalidOperationException("At most 64 widgets can be pinned."); }
        pins.Add(pin);
    }

    internal static void Move(List<PluginWidgetPin> pins, PluginWidgetPin pin, int offset)
    {
        int index = pins.IndexOf(pin);
        if (index < 0 || offset is not (-1 or 1)) { return; }
        int destination = Math.Clamp(index + offset, 0, pins.Count - 1);
        (pins[index], pins[destination]) = (pins[destination], pins[index]);
    }

    internal static void ResetOrder(List<PluginWidgetPin> pins) => pins.Sort((left, right) =>
    {
        int plugin = string.CompareOrdinal(left.PluginId, right.PluginId);
        if (plugin != 0) { return plugin; }
        int instance = string.CompareOrdinal(left.InstanceId, right.InstanceId);
        return instance != 0 ? instance : string.CompareOrdinal(left.WidgetId, right.WidgetId);
    });

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => !char.IsControl(character));
}
