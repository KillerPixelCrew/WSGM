using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The outcome of moving a title to its next launch mode or route.</summary>
/// <param name="Command">Whether the change was made or refused.</param>
/// <param name="NeedsAcknowledgement">
///     Whether nothing changed because the user has to accept the risk of the next mode first.
/// </param>
public sealed record GameLibraryLaunchCycle(SteamUiCommandResult Command, bool NeedsAcknowledgement = false);
