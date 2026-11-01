using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the Store says about one title.</summary>
/// <param name="ProductId">The Store product id.</param>
/// <param name="Name">The title as the Store names it.</param>
/// <param name="IsGame">Whether the Store classifies it as a game.</param>
/// <param name="Multiplayer">Whether it reports a multiplayer capability.</param>
/// <param name="MultiplayerEvidence">Which capability decided that, in one sentence.</param>
/// <param name="Images">The artwork the Store offers.</param>
public sealed record StoreCatalogEntry(
    string ProductId,
    string Name,
    bool IsGame,
    MultiplayerVerdict Multiplayer,
    string MultiplayerEvidence,
    IReadOnlyList<StoreCatalogImage> Images);
