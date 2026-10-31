using System;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Host-owned persistence operations for widget pins and order.</summary>
/// <param name="Read">Returns the current pins; callers must not mutate the returned array.</param>
/// <param name="Set">Pins or unpins one widget when the boolean is true or false.</param>
/// <param name="Move">Moves one pin by a signed relative offset.</param>
/// <param name="Remove">Removes one saved pin.</param>
/// <param name="Reset">Resets saved ordering without invoking plugin actions.</param>
internal sealed record PluginWidgetPreferences(
    Func<Task<PluginWidgetPin[]>> Read,
    Func<PluginWidgetPin, bool, Task> Set,
    Func<PluginWidgetPin, int, Task> Move,
    Func<PluginWidgetPin, Task> Remove,
    Func<Task> Reset);
