using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>One declared setting and the value in force.</summary>
/// <param name="Setting">What the plugin declared.</param>
/// <param name="Value">The saved value, or the declared default.</param>
internal sealed record CommonPluginSettingValue(PluginSetting Setting, PluginValue Value);
