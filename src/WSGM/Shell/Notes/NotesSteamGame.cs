namespace WSGM.Shell;

/// <summary>The running game as Steam's Notes page shows it.</summary>
/// <param name="State">game, none or unknown.</param>
/// <param name="Scope">Its notebook.</param>
/// <param name="Title">Its name.</param>
/// <param name="Detail">Why it is unknown.</param>
internal sealed record NotesSteamGame(string State, string? Scope, string Title, string? Detail);
