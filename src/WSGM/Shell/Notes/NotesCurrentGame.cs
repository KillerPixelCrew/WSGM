namespace WSGM.Shell;

/// <summary>The running game as Notes sees it.</summary>
/// <param name="State">Whether there is one.</param>
/// <param name="Scope">Its notebook, for <see cref="NotesGameState.Game" />.</param>
/// <param name="Title">Its display name.</param>
/// <param name="Detail">Why the identity is unknown, for <see cref="NotesGameState.Unknown" />.</param>
internal sealed record NotesCurrentGame(NotesGameState State, string? Scope, string Title, string? Detail = null)
{
    internal static NotesCurrentGame NoGame { get; } = new(NotesGameState.None, null, "No game running");
}
