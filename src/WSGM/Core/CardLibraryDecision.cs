using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>What Steam's registrations at one card path need doing to them.</summary>
public enum CardLibraryAction
{
    /// <summary>Steam's view already matches the card that is in the reader.</summary>
    None,

    /// <summary>Registrations exist for a library that is not on this volume any
    /// more; remove them and add nothing.</summary>
    Purge,

    /// <summary>Registrations exist for a DIFFERENT card, and the card now in the
    /// reader carries its own library; replace them.</summary>
    Replace,

    /// <summary>The card carries a library Steam does not know about; add it.</summary>
    Add,
}

/// <summary>Decides what a card insertion or removal means for Steam's install-folder
/// list. Pure, so the rule can be tested without a Steam client, a card reader, or a
/// card.</summary>
/// <remarks>
/// <para>
/// The problem this exists to solve is that a card reader keeps ONE drive letter for
/// every card that passes through it, while Steam keys its install folders by PATH and
/// never dedupes them. Swap a card and <c>E:\SteamLibrary</c> now means a different
/// library, but Steam is still holding the previous one — with its app list, its
/// capacity and its content id. Nothing in Steam notices, ejecting does not clear it
/// (the registration was never tied to the card), and only a restart rebuilds the list
/// from disk. Adding the new card on top then leaves TWO registrations at one path,
/// which is what surfaces as "the new card shows the previous card's games but the
/// right size".
/// </para>
/// <para>
/// The identity that settles it is the card's own <c>libraryfolder.vdf</c> content id,
/// which travels with the card. Steam's live folder API does not expose content ids at
/// all, so the comparison is against the ids registered for that path in
/// <c>config\libraryfolders.vdf</c> — the same file the card manager already reads.
/// </para>
/// </remarks>
public static class CardLibraryDecision
{
    /// <summary>Decides what to do with the registrations at one card path.</summary>
    /// <param name="cardContentId">The content id read from the volume's own
    /// <c>SteamLibrary\libraryfolder.vdf</c>, or null when the volume carries no
    /// Steam library (a blank card, or one formatted by something else).</param>
    /// <param name="registeredContentIds">The content ids Steam has registered AT
    /// THAT PATH. Usually zero or one; more than one is the duplicate state this
    /// whole mechanism exists to clear.</param>
    /// <returns>The action to apply.</returns>
    public static CardLibraryAction Decide(
        string? cardContentId, IReadOnlyCollection<string> registeredContentIds)
    {
        ArgumentNullException.ThrowIfNull(registeredContentIds);
        var registered = registeredContentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (string.IsNullOrWhiteSpace(cardContentId))
        {
            // Nothing on the volume claims to be a Steam library, so anything Steam
            // still lists at this path belongs to a card that has left the reader.
            return registered.Count > 0 ? CardLibraryAction.Purge : CardLibraryAction.None;
        }
        if (registered.Count == 0)
        {
            return CardLibraryAction.Add;
        }
        // Exactly the one registration, and it is this card's: leave it alone. Any
        // other shape - a different id, or this id sitting next to a stale duplicate -
        // has to be rebuilt, because Steam offers no way to drop just one of them by
        // identity.
        return registered.Count == 1
            && string.Equals(registered[0], cardContentId, StringComparison.Ordinal)
                ? CardLibraryAction.None
                : CardLibraryAction.Replace;
    }
}
