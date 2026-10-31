namespace WSGM.Shell;

/// <summary>What the running game resolves to for Notes.</summary>
internal enum NotesGameState
{
    /// <summary>A game is running and has a stable identity.</summary>
    Game,

    /// <summary>No game is running.</summary>
    None,

    /// <summary>Something is running but its identity is unknown or ambiguous right now.</summary>
    Unknown
}
