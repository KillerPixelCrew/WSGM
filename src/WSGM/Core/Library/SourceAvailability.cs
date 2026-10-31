namespace WSGM.Core;

/// <summary>Whether a source's launcher is on this machine.</summary>
/// <param name="Installed">Whether it was found.</param>
/// <param name="Detail">What was found or why not, in one short phrase the sidebar shows.</param>
public sealed record SourceAvailability(bool Installed, string Detail)
{
    /// <summary>A launcher that is not installed.</summary>
    public static SourceAvailability NotFound { get; } = new(false, "Not found");
}
