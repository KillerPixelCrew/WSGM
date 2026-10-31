namespace WSGM.Shell;

/// <summary>One outcome from applying an audio-profile value.</summary>
/// <param name="Name">User-facing name of the attempted setting.</param>
/// <param name="Succeeded">Whether Windows accepted this operation; no confirming readback is required.</param>
/// <param name="Detail">Outcome or refusal detail suitable for display.</param>
internal sealed record AudioProfileOperationResult(string Name, bool Succeeded, string Detail);
