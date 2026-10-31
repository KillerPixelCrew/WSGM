namespace WSGM.Core;

/// <summary>A snapshot of the shim deployment.</summary>
/// <param name="State">What the deployment looks like on disk.</param>
/// <param name="Vector">Which name the shim occupies, if any.</param>
/// <param name="Detail">Extra context for the Settings status line and the log.</param>
internal readonly record struct SteamInputShimStatus(
    SteamInputShimState State,
    SteamInputShimVector Vector,
    string? Detail);
