namespace WSGM.Shell;

/// <summary>Where one title's gathering stands.</summary>
/// <param name="Status">The status.</param>
/// <param name="Detail">Why, for a failed or unavailable title; otherwise empty.</param>
/// <param name="MatchName">The game it was matched to, or empty.</param>
internal readonly record struct GameLibraryArtworkProgress(
    GameLibraryArtworkStatus Status,
    string Detail,
    string MatchName);
