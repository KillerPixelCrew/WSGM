using System;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>The wording both Graphics surfaces use for a graphics row.</summary>
internal static class GraphicsCapabilityText
{
    /// <summary>The vendor dropdown title in Quick Access.</summary>
    /// <param name="name">Publisher display name.</param>
    /// <returns>The name with a trailing Graphics replaced by GPU, an existing GPU suffix retained, or GPU appended.</returns>
    internal static string PublisherTitle(string name)
    {
        const string graphicsSuffix = " Graphics";
        return name.EndsWith(graphicsSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^graphicsSuffix.Length] + " GPU"
            : name.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + " GPU";
    }

    /// <summary>What a row says about when its value takes effect, or null when it does at once.</summary>
    /// <param name="timing">The descriptor's apply timing.</param>
    /// <returns>The note.</returns>
    internal static string? TimingNote(CapabilityApplyTiming timing)
    {
        return timing switch
        {
            CapabilityApplyTiming.NextApplicationStart => "Applies when a game next starts",
            CapabilityApplyTiming.SystemRestart => "Applies after restart",
            _ => null
        };
    }

    /// <summary>What a publisher's page says about its health, or null while it is ready.</summary>
    /// <param name="name">The publisher's name.</param>
    /// <param name="health">Its health.</param>
    /// <param name="detail">Its own detail, or null.</param>
    /// <returns>The note.</returns>
    internal static string? HealthNote(string name, PluginHealth health, string? detail)
    {
        return health switch
        {
            PluginHealth.Ready => null,
            PluginHealth.Failed => $"{name} stopped working. {detail}".TrimEnd(),
            _ => $"{name} is waiting for its driver. {detail}".TrimEnd()
        };
    }
}
