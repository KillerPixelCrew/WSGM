namespace WSGM.Core;

/// <summary>The outcome of a transaction.</summary>
/// <param name="Manifest">The resulting manifest, or null when nothing is installed.</param>
/// <param name="Notice">A user-facing summary, including settings that were unavailable.</param>
internal sealed record GameToolOutcome(GameToolManifest? Manifest, string Notice);
