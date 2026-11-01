namespace WSGM.Core;

/// <summary>An installed community plugin the release no longer carries.</summary>
/// <param name="PluginId">The plugin id.</param>
/// <param name="Name">The plugin's display name.</param>
/// <param name="Contact">The developer contact, when the plugin names one.</param>
/// <param name="Log">The failed build's log, when the release recorded it.</param>
public sealed record UpdateWarning(string PluginId, string Name, string? Contact, string? Log);
