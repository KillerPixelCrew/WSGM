namespace WSGM.Shell;

/// <summary>Whether a preset was applied and, when it was not, why.</summary>
/// <param name="Succeeded">Whether every step of the preset was applied.</param>
/// <param name="Error">Why it was not applied, or null on success.</param>
internal sealed record PowerPresetApplyResult(bool Succeeded, string? Error);
