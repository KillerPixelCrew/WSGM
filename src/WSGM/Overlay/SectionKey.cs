using System;

namespace WSGM.Overlay;

/// <summary>A Device section's bare id and the two persisted keys built from it.</summary>
/// <param name="Id">Unprefixed shared device-section identifier, such as <c>plugin.power</c>.</param>
internal readonly record struct SectionKey(string Id)
{
    private const string PinPrefix = "section.device.";
    private const string RailPrefix = "device.section.";

    /// <summary>The Quick Access pin and heading id of the section.</summary>
    internal string PinId => PinPrefix + Id;

    /// <summary>The section rail key of the section's page.</summary>
    internal string RailKey => RailPrefix + Id;

    /// <summary>Decodes a pin id or rail key.</summary>
    /// <param name="key">A section.device. or device.section. prefixed key.</param>
    /// <returns>The unprefixed section identity; unknown prefixes throw ArgumentException.</returns>
    internal static SectionKey FromKey(string key)
    {
        if (key.StartsWith(PinPrefix, StringComparison.Ordinal))
        {
            return new SectionKey(key[PinPrefix.Length..]);
        }

        if (key.StartsWith(RailPrefix, StringComparison.Ordinal))
        {
            return new SectionKey(key[RailPrefix.Length..]);
        }

        throw new ArgumentException("Unknown device section key.", nameof(key));
    }
}
