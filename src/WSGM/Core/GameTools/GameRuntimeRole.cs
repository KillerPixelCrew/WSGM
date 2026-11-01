namespace WSGM.Core;

/// <summary>What a candidate executable most likely is.</summary>
internal enum GameRuntimeRole
{
    /// <summary>The program that renders the game.</summary>
    Game,

    /// <summary>A launcher, starter or settings program that starts the game.</summary>
    Launcher,

    /// <summary>A redistributable, installer, crash reporter or anti-cheat helper.</summary>
    Helper,

    /// <summary>Nothing identifies it either way.</summary>
    Unknown
}
