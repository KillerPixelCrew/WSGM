namespace WSGM.Shell;

/// <summary>Whether an installed core's declared system files are present.</summary>
/// <param name="InstallationId">The installation the core belongs to.</param>
/// <param name="CoreId">The core.</param>
/// <param name="Missing">Whether any declared file is absent.</param>
internal sealed record EmulatorCoreStatus(string InstallationId, string CoreId, bool Missing)
{
    /// <summary>The absent files' names, in the order the core declares them.</summary>
    public string[] MissingFiles { get; init; } = [];
}
