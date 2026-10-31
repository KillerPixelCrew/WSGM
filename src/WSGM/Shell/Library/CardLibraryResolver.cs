using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Resolves <see cref="FilterKind.SdCard" /> membership from WSGM's card
///     model: "inserted" = union of currently-present cards, "any" = union of all
///     tracked cards, "specific" = one card's remembered app ids.
/// </summary>
/// <param name="config">The configuration holding the card model.</param>
/// <param name="discovered">The cards the sync's scan found mounted.</param>
internal sealed class CardLibraryResolver(AppConfig config, List<DiscoveredCard> discovered) : ISdCardResolver
{
    private readonly HashSet<string> _present = new(
        discovered.Select(d => d.ContentId), StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyCollection<long> Resolve(SdCardScope scope, string contentId)
    {
        var cards = scope switch
        {
            SdCardScope.Inserted => config.CardLibraries.Where(c => _present.Contains(c.ContentId)),
            SdCardScope.Any => config.CardLibraries,
            _ => config.CardLibraries.Where(c => string.Equals(c.ContentId, contentId, StringComparison.Ordinal))
        };
        var ids = new HashSet<long>();
        foreach (var card in cards)
        {
            foreach (var id in card.AppIds)
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
