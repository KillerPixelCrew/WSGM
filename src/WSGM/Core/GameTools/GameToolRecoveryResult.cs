namespace WSGM.Core;

/// <summary>What recovery did to a game's record.</summary>
/// <param name="Record">The record after recovery and rebasing, or null when the game has none.</param>
/// <param name="Notice">A user-facing summary of what was undone, or null when nothing was.</param>
/// <remarks>
///     A change that still waits, because the game may be running or its folder couldn't be reached, and one whose
///     rollback failed stay visible through their manifests' states in <paramref name="Record" />.
/// </remarks>
internal sealed record GameToolRecoveryResult(GameToolRecord? Record, string? Notice);
