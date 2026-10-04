using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The library badge's state: which library holds which game, built from the card model and
///     Steam's library registrations and published through the toolkit's
///     <see cref="SteamLibraryBadgeSurface" />.
/// </summary>
/// <remarks>
///     One process-wide reading, because it projects one process-wide model: the card libraries in
///     <see cref="AppConfig" /> and Steam's <c>libraryfolders.vdf</c>. It is rebuilt by every
///     library-tab sync, which already runs on every card change and on boot, and the session host
///     publishes it on <see cref="Changed" />. Every tracked card is listed, hidden or absent: hiding
///     governs the tab, not where the game is, and a game on an absent card keeps naming the card.
///     Every other registered library is named by its Steam label, or by its drive when it has none.
///     WSGM names every library, so a game no listed library holds gets no badge.
/// </remarks>
internal static class LibraryBadges
{
    private static readonly Lock Gate = new();
    private static SteamLibraryBadgeState? _current;
    private static long _revision;

    /// <summary>The libraries the badge names, or null before the card model has been read once.</summary>
    internal static SteamLibraryBadgeState? Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after <see cref="Update" /> replaced the reading with a different one.</summary>
    internal static event Action? Changed;

    /// <summary>
    ///     Rebuilds the reading from the card model, the cards present right now and Steam's library
    ///     registrations.
    /// </summary>
    /// <param name="config">The configuration holding the tracked card libraries.</param>
    /// <param name="presentContentIds">The content ids of the cards attached right now.</param>
    /// <remarks>
    ///     Every tab sync calls this. Steam's <c>libraryfolders.vdf</c> is read here; an unreadable one
    ///     leaves the card libraries only. A reading equal to the current one keeps its revision and
    ///     raises nothing, so an unchanged model is not published to Steam again.
    /// </remarks>
    internal static void Update(AppConfig config, IReadOnlySet<string> presentContentIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(presentContentIds);
        var libraryFolders = ReadLibraryFolders();
        lock (Gate)
        {
            var next = Build(config, presentContentIds, libraryFolders, _revision + 1);
            if (_current is not null && SameLibraries(_current.Libraries, next.Libraries))
            {
                return;
            }

            _revision++;
            _current = next;
        }

        Changed?.Invoke();
    }

    private static string? ReadLibraryFolders()
    {
        try
        {
            return Steam.TryReadLibraryFolders(out _, out var text) ? text : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Library badge: libraryfolders.vdf could not be read; naming card libraries only: {error.Message}");
            return null;
        }
    }

    private static bool SameLibraries(
        IReadOnlyList<SteamLibraryBadgeLibrary> current,
        IReadOnlyList<SteamLibraryBadgeLibrary> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].Name, next[i].Name, StringComparison.Ordinal)
                || current[i].Connected != next[i].Connected
                || !current[i].AppIds.SequenceEqual(next[i].AppIds))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The reading for one card model and one registration file, without publishing it.</summary>
    /// <param name="config">The configuration holding the tracked card libraries.</param>
    /// <param name="presentContentIds">The content ids of the cards attached right now.</param>
    /// <param name="libraryFolders">Steam's <c>libraryfolders.vdf</c> text, or null when it could not be read.</param>
    /// <param name="revision">The revision to stamp.</param>
    /// <returns>
    ///     Every named card with games, attached or not, then every other registration with games,
    ///     named by its label or its drive. A registered card is listed once, under its card name.
    /// </returns>
    internal static SteamLibraryBadgeState Build(
        AppConfig config, IReadOnlySet<string> presentContentIds, string? libraryFolders, long revision = 0)
    {
        var cardIds = config.CardLibraries
            .Select(static card => card.ContentId)
            .ToHashSet(StringComparer.Ordinal);
        List<SteamLibraryVdf.LibraryRegistration> registrations =
            libraryFolders is null ? [] : SteamLibraryVdf.ReadRegistrations(libraryFolders);
        IReadOnlyList<SteamLibraryBadgeLibrary> libraries =
        [
            .. config.CardLibraries
                .Where(card => !string.IsNullOrWhiteSpace(card.Name) && card.AppIds.Count > 0)
                .Select(card => new SteamLibraryBadgeLibrary(
                    card.Name, presentContentIds.Contains(card.ContentId), [.. card.AppIds])),
            .. registrations
                .Where(registration => registration.AppIds.Count > 0
                                       && (registration.ContentId is null
                                           || !cardIds.Contains(registration.ContentId)))
                .Select(static registration => new SteamLibraryBadgeLibrary(
                    RegistrationName(registration), true, registration.AppIds))
                .Where(static library => library.Name.Length > 0)
        ];
        return new SteamLibraryBadgeState(libraries, revision);
    }

    /// <summary>A registration's Steam label, or its drive (for example <c>D:</c>) when it has none.</summary>
    private static string RegistrationName(SteamLibraryVdf.LibraryRegistration registration)
    {
        var label = registration.Label.Trim();
        return label.Length > 0
            ? label
            : SteamLibraryVdf.VolumeRoot(registration.Path).TrimEnd('\\');
    }
}

/// <summary>Hears what the library badge reports back: Steam's Big Picture Home layout.</summary>
/// <remarks>
///     The badge sits on the tile and moves with the carousel in either layout, so nothing here
///     changes placement. The report is logged on every transition so a pasted <c>wsgm.log</c> says
///     which layout Steam was in, which is the fact the September 2026 beta made worth knowing.
///     <see cref="Log.Change" /> already writes only a changed line.
/// </remarks>
internal sealed class LibraryBadgeBackend : ISteamLibraryBadgeBackend
{
    /// <inheritdoc />
    public Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken)
    {
        Log.Change("steam.home.layout", $"Steam Home layout: Big Art Mode {(bigArt ? "on" : "off")}.");
        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}
