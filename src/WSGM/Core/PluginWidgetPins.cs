using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Core;

/// <summary>Persistent widget identity, independent of runtime generation and display order.</summary>
/// <param name="PluginId">Owning plugin identity.</param>
/// <param name="InstanceId">Configured plugin instance.</param>
/// <param name="WidgetId">Stable widget declaration identity.</param>
public sealed record PluginWidgetPin(string PluginId, string InstanceId, string WidgetId);

/// <summary>Pin ordering that retains unavailable plugin identities.</summary>
internal static class PluginWidgetPins
{
    /// <summary>Copies valid persistent widget identities, dropping nulls and exact duplicates.</summary>
    /// <param name="pins">Saved pins, or null for an empty list.</param>
    /// <returns>A new mutable list retaining the first valid occurrence in order.</returns>
    internal static List<PluginWidgetPin> Normalize(IEnumerable<PluginWidgetPin?>? pins)
    {
        return
        [
            .. (pins ?? []).OfType<PluginWidgetPin>()
            .Where(pin => Valid(pin.PluginId) && Valid(pin.InstanceId) && Valid(pin.WidgetId))
            .Distinct()
        ];
    }

    /// <summary>Adds a valid pin once or removes every occurrence of it.</summary>
    /// <param name="pins">Exclusive caller-owned list to mutate.</param>
    /// <param name="pin">Complete valid package, instance, and widget identity.</param>
    /// <param name="pinned">True appends when absent; false removes matching entries.</param>
    /// <exception cref="ArgumentException">The pin identity fails plain-text validation.</exception>
    internal static void Set(List<PluginWidgetPin> pins, PluginWidgetPin pin, bool pinned)
    {
        if (Normalize([pin]).Count == 0)
        {
            throw new ArgumentException("Invalid widget identity.", nameof(pin));
        }

        if (!pinned)
        {
            pins.RemoveAll(item => item == pin);
            return;
        }

        if (pins.Contains(pin))
        {
            return;
        }

        pins.Add(pin);
    }

    /// <summary>Moves one pin by swapping it with its adjacent neighbor.</summary>
    /// <param name="pins">Caller-owned list to mutate.</param>
    /// <param name="pin">Identity to move; an absent pin is ignored.</param>
    /// <param name="offset">-1 for earlier or 1 for later; other values are ignored and endpoints clamp.</param>
    internal static void Move(List<PluginWidgetPin> pins, PluginWidgetPin pin, int offset)
    {
        var index = pins.IndexOf(pin);
        if (index < 0 || offset is not (-1 or 1))
        {
            return;
        }

        var destination = Math.Clamp(index + offset, 0, pins.Count - 1);
        (pins[index], pins[destination]) = (pins[destination], pins[index]);
    }

    /// <summary>Sorts pins ordinally by package, instance, then widget identity.</summary>
    /// <param name="pins">Caller-owned list to reorder in place.</param>
    internal static void ResetOrder(List<PluginWidgetPin> pins)
    {
        pins.Sort((left, right) =>
        {
            var plugin = string.CompareOrdinal(left.PluginId, right.PluginId);
            if (plugin != 0)
            {
                return plugin;
            }

            var instance = string.CompareOrdinal(left.InstanceId, right.InstanceId);
            return instance != 0 ? instance : string.CompareOrdinal(left.WidgetId, right.WidgetId);
        });
    }

    private static bool Valid(string? value)
    {
        return PlainText.TryValidate(value, "widget identity", out _);
    }
}
