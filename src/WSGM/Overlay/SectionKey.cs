using System;

namespace WSGM.Overlay;

/// <summary>Stable device section identity with distinct focus and persisted-pin encodings.</summary>
/// <param name="Id">Unprefixed shared device-section identifier.</param>
internal readonly record struct SectionKey(string Id)
{
    private const string FocusPrefix = "section.device.";
    private const string PinPrefix = "device.section.";

    /// <summary>Semantic focus key for the source section heading.</summary>
    internal string FocusKey => FocusPrefix + Id;

    /// <summary>Persisted Quick Access pin key for this section.</summary>
    internal string PinKey => PinPrefix + Id;

    /// <summary>Decodes a recognized focus or pin key.</summary>
    /// <param name="key">A section.device. or device.section. prefixed key.</param>
    /// <returns>The unprefixed section identity; unknown prefixes throw ArgumentException.</returns>
    internal static SectionKey FromKey(string key)
    {
        if (key.StartsWith(FocusPrefix, StringComparison.Ordinal))
        {
            return new SectionKey(key[FocusPrefix.Length..]);
        }

        if (key.StartsWith(PinPrefix, StringComparison.Ordinal))
        {
            return new SectionKey(key[PinPrefix.Length..]);
        }

        throw new ArgumentException("Unknown device section key.", nameof(key));
    }
}
