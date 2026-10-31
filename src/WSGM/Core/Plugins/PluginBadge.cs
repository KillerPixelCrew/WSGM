namespace WSGM.Core;

/// <summary>One badge on a Plugins page row.</summary>
/// <param name="Text">What it says.</param>
/// <param name="Tone">Its colour.</param>
public sealed record PluginBadge(string Text, PluginBadgeTone Tone);
