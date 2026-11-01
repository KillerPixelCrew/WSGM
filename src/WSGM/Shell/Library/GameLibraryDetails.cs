using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>What the review knows about one title beyond its card: the evidence behind it.</summary>
/// <param name="InstallPath">Where it is installed, or empty.</param>
/// <param name="Identity">Its identity in its source, for diagnosis.</param>
/// <param name="LaunchEvidence">Why its launch route works, and what it cannot do.</param>
/// <param name="Multiplayer">Whether it is known to have multiplayer.</param>
/// <param name="MultiplayerEvidence">Why, in one sentence.</param>
/// <param name="Notes">Anything else worth showing.</param>
public sealed record GameLibraryDetails(
    string InstallPath,
    string Identity,
    string LaunchEvidence,
    string Multiplayer,
    string MultiplayerEvidence,
    IReadOnlyList<string> Notes);
