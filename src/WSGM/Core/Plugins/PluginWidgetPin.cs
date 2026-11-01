namespace WSGM.Core;

/// <summary>Persistent widget identity, independent of runtime generation and display order.</summary>
/// <param name="PluginId">Owning plugin identity.</param>
/// <param name="InstanceId">Configured plugin instance.</param>
/// <param name="WidgetId">Stable widget declaration identity.</param>
public sealed record PluginWidgetPin(string PluginId, string InstanceId, string WidgetId);
