namespace WSGM.Core;

/// <summary>Pack coverage against one observed Steam resource vocabulary.</summary>
/// <param name="SupportedResources">Resources with at least one usable file.</param>
/// <param name="MissingResources">Resources with no usable file.</param>
/// <param name="IgnoredResources">Resources explicitly excluded by the pack.</param>
/// <param name="UnknownMappings">Manifest mappings absent from the current Steam vocabulary.</param>
/// <param name="AssetProblems">Missing, empty, unsupported or unreadable asset details.</param>
internal sealed record SoundPackCompatibility(
    string[] SupportedResources,
    string[] MissingResources,
    string[] IgnoredResources,
    string[] UnknownMappings,
    string[] AssetProblems)
{
    /// <summary>Counts and fallback behavior suitable for the pack-details surface.</summary>
    internal string Summary =>
        $"{SupportedResources.Length} mapped resources; {MissingResources.Length} missing resources; {IgnoredResources.Length} ignored resources; {AssetProblems.Length} missing or unsupported assets; {UnknownMappings.Length} unknown mappings. Unmatched events use Steam defaults.";
}
