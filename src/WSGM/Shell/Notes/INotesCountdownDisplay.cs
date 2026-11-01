namespace WSGM.Shell;

/// <summary>The countdown shown over the game while the player prepares.</summary>
internal interface INotesCountdownDisplay
{
    /// <summary>Shows or updates the countdown.</summary>
    /// <param name="remaining">Seconds left.</param>
    /// <param name="game">The game being captured.</param>
    void Show(int remaining, string game);

    /// <summary>Hides the countdown; safe to call when hidden.</summary>
    void Hide();
}
