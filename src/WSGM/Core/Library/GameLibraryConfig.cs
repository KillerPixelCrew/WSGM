namespace WSGM.Core;

/// <summary>How the Game Library treats titles it has not been told anything specific about.</summary>
/// <remarks>
///     Persisted as <see cref="AppConfig.GameLibrary" /> and edited on Settings' Steam page, because
///     it configures WSGM's own feature. What the user decides about one title is not here: those
///     are the library's stored per-title choices.
/// </remarks>
public sealed class GameLibraryConfig
{
    /// <summary>
    ///     The launch mode a newly found single-player title starts on. A multiplayer title starts
    ///     controller-only whatever this says, and a title with no validated route can only be
    ///     controller-only.
    /// </summary>
    public ImportMode DefaultMode { get; set; } = ImportMode.SteamIntegration;

    /// <summary>
    ///     Whether titles with no validated launch route are offered at all. They can only ever be
    ///     controller-only, so they are hidden unless asked for.
    /// </summary>
    public bool ImportUnroutable { get; set; }
}
