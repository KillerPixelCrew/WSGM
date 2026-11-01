namespace WSGM.Core;

/// <summary>Outcome of an artwork change.</summary>
/// <param name="Detail">
///     A user-facing note (why it failed, or a follow-up such as
///     "restart Steam").
/// </param>
/// <param name="Succeeded">Whether Steam accepted the requested change.</param>
internal readonly record struct ArtworkResult(string Detail, bool Succeeded = false);
