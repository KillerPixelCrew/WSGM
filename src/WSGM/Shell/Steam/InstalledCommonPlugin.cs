namespace WSGM.Shell;

/// <summary>An installed common plugin package.</summary>
/// <param name="PluginId">Its package identity.</param>
/// <param name="Name">Its display name.</param>
/// <param name="EnabledByDefault">
///     Whether its default instance runs while the configuration names none, as a graphics package does on
///     a machine with an adapter it serves.
/// </param>
internal sealed record InstalledCommonPlugin(string PluginId, string Name, bool EnabledByDefault = false);
