using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>One graphics publisher as the Graphics pages name it.</summary>
/// <param name="PluginId">The graphics plugin's id.</param>
/// <param name="Name">Its package's display name.</param>
/// <param name="Health">Its last reported health.</param>
/// <param name="Note">What to say about that health, or null while it is ready.</param>
internal sealed record GraphicsOverlayPublisher(string PluginId, string Name, PluginHealth Health, string? Note);
