namespace WSGM.Core;

/// <summary>Whether a title is known to have multiplayer, which decides its default input mode.</summary>
public enum MultiplayerVerdict
{
    /// <summary>Nothing said either way. Treated as single-player for the default.</summary>
    Unknown,

    /// <summary>No multiplayer capability was reported.</summary>
    SinglePlayer,

    /// <summary>A multiplayer capability was reported.</summary>
    Multiplayer
}
