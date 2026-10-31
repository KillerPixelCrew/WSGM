using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     Reads what the tab builder and the card manager pick from out of Steam's library: store tags,
///     collections, the games, and the names of a card's games. A failed read keeps the items the
///     picker already had and names the error.
/// </summary>
internal static class LibraryPickerReader
{
    /// <summary>Reads Steam's store tags for the Tag filter.</summary>
    /// <param name="steam">The session's Steam client, or null on a surface without one.</param>
    /// <param name="current">Reads the picker as it is now, for a failed read to keep.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker after the read.</returns>
    internal static async Task<LibraryTagPicker> ReadTagsAsync(SteamClient? steam, Func<LibraryTagPicker> current,
        CancellationToken cancellationToken)
    {
        if (steam is null)
        {
            return current() with { Loading = false, Error = "Steam is not available here." };
        }

        try
        {
            var read = await steam.Library.ReadStoreTagsAsync(cancellationToken).ConfigureAwait(false);
            var previous = current();
            return read is { Succeeded: true, Value: { } tags }
                ? new LibraryTagPicker([.. tags.Select(t => new LibraryTagChoice(t.TagId, t.Name, t.Count))],
                    true, false, null)
                : previous with { Loading = false, Error = read.Error ?? "Steam did not list its tags." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return current() with { Loading = false, Error = ex.Message };
        }
    }

    /// <summary>Reads Steam's collections for the Collection filter.</summary>
    /// <param name="steam">The session's Steam client, or null on a surface without one.</param>
    /// <param name="current">Reads the picker as it is now, for a failed read to keep.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker after the read.</returns>
    internal static async Task<LibraryCollectionPicker> ReadCollectionsAsync(SteamClient? steam,
        Func<LibraryCollectionPicker> current, CancellationToken cancellationToken)
    {
        if (steam is null)
        {
            return current() with
            {
                Loading = false, Error = "Steam is not available here."
            };
        }

        try
        {
            var read = await steam.Library.ReadCollectionsAsync(cancellationToken).ConfigureAwait(false);
            var previous = current();
            return read is { Succeeded: true, Value: { } collections }
                ? new LibraryCollectionPicker(
                    [.. collections.Select(c => new LibraryCollectionChoice(c.Id, c.Name, c.AppIds.Count))],
                    true, false, null)
                : previous with
                {
                    Loading = false, Error = read.Error ?? "Steam did not list its collections."
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return current() with { Loading = false, Error = ex.Message };
        }
    }

    /// <summary>Reads the library for the whitelist and blacklist filters.</summary>
    /// <param name="steam">The session's Steam client, or null on a surface without one.</param>
    /// <param name="current">Reads the picker as it is now, for a failed read to keep.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The picker after the read.</returns>
    internal static async Task<LibraryGamePicker> ReadGamesAsync(SteamClient? steam, Func<LibraryGamePicker> current,
        CancellationToken cancellationToken)
    {
        var read = await OverlayLibraryLookup.ReadAsync(steam, cancellationToken).ConfigureAwait(false);
        var previous = current();
        return read.Succeeded
            ? new LibraryGamePicker([.. read.Games.Select(g => new LibraryGameChoice(g.AppId, g.Name))], true,
                false, null)
            : previous with { Loading = false, Error = read.Error };
    }

    /// <summary>A card's games by id alone, while their names are being read.</summary>
    /// <param name="contentId">The card.</param>
    /// <param name="ids">The card's remembered app ids.</param>
    /// <returns>The loading list.</returns>
    internal static LibraryCardGames Unnamed(string contentId, IReadOnlyList<long> ids)
    {
        return new LibraryCardGames(contentId, Named(ids, null), true, null);
    }

    /// <summary>Names the games on a card from Steam's library; the ids alone when it did not answer.</summary>
    /// <param name="steam">The session's Steam client, or null on a surface without one.</param>
    /// <param name="contentId">The card.</param>
    /// <param name="ids">The card's remembered app ids.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The named list.</returns>
    internal static async Task<LibraryCardGames> ReadCardGamesAsync(SteamClient? steam, string contentId,
        IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var read = await OverlayLibraryLookup.ReadAsync(steam, cancellationToken).ConfigureAwait(false);
        var names = read.Games.ToDictionary(game => (long)game.AppId, game => game.Name);
        return new LibraryCardGames(contentId, Named(ids, names), false, read.Error);
    }

    private static IReadOnlyList<LibraryCardGame> Named(IReadOnlyList<long> ids, Dictionary<long, string>? names)
    {
        return [.. ids.Select(id => new LibraryCardGame(id,
            names is not null && names.TryGetValue(id, out var name)
                ? name
                : id.ToString(CultureInfo.InvariantCulture)))];
    }
}
