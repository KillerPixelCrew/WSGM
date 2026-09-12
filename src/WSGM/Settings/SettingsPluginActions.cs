using System;
using System.Collections.Generic;

namespace WSGM.Settings;

/// <summary>The actions running plugins declare, for the Settings window to offer.
///
/// Process-wide because whether this process has a resident plugin host is a process-wide fact, not
/// a property of any one window. Settings is opened from the tray, from the overlay and as a
/// standalone <c>--settings</c> process, and only the first two run inside a shell session; none of
/// them should have to carry a host reference for the benefit of one page. A standalone Settings
/// simply reads an empty list and shows saved steps read-only, which is the honest rendering: it has
/// no plugin to ask.</summary>
internal static class SettingsPluginActions
{
    private static Func<IReadOnlyList<SettingsViewModel.PluginActionOption>>? _source;

    /// <summary>Publishes the resident session's action source. Called once at shell start.</summary>
    /// <param name="source">Reads the currently running instances' declared actions.</param>
    internal static void Publish(Func<IReadOnlyList<SettingsViewModel.PluginActionOption>> source) =>
        _source = source;

    /// <summary>Withdraws the source, so a Settings window outliving the session shows nothing
    /// rather than reaching into a disposed host.</summary>
    internal static void Withdraw() => _source = null;

    /// <summary>Reads the declared actions, or an empty list when this process has no host.</summary>
    /// <returns>One entry per action of every running instance.</returns>
    internal static IReadOnlyList<SettingsViewModel.PluginActionOption> Read()
    {
        try { return _source?.Invoke() ?? []; }
        catch (Exception) { return []; }
    }
}
