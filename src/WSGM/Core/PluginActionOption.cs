using WSGM.Plugin.Sdk;

namespace WSGM.Core;

/// <summary>One action a running plugin instance offers, for the action lists.</summary>
/// <param name="Identity">The plugin instance.</param>
/// <param name="Action">The declared action.</param>
/// <param name="Label">How to name it in a picker.</param>
public sealed record PluginActionOption(
    PluginInstanceIdentity Identity,
    PluginAction Action,
    string Label)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Label;
    }
}
