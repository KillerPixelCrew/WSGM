namespace WSGM.Shell;

/// <summary>Sanitized direct-library family information for standalone Settings.</summary>
/// <param name="FamilyId">Stable handheld family identifier; no native path or private state is exposed.</param>
/// <param name="Version">Direct library assembly version.</param>
internal sealed record HandheldDiagnostic(
    string FamilyId,
    string Version);
