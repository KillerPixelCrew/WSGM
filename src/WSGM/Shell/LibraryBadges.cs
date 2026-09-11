using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// The library badge's state: which tracked library holds which game, built from the card model
/// and published through the toolkit's <see cref="SteamLibraryBadgeSurface"/>.
/// </summary>
/// <remarks>
/// One process-wide reading, because it projects one process-wide model: the card libraries in
/// <see cref="AppConfig"/>. It is rebuilt by every library-tab sync, which already runs on every
/// card change and on boot, and the session host publishes it on <see cref="Changed"/>. Every
/// tracked card is listed, hidden or absent: hiding governs the tab, not where the game is, and a
/// game on an absent card keeps naming the card rather than reading as internal.
/// </remarks>
internal static class LibraryBadges
{
    private static readonly object Gate = new();
    private static SteamLibraryBadgeState? _current;
    private static long _revision;

    /// <summary>Raised after <see cref="Update"/> replaced the reading.</summary>
    internal static event Action? Changed;

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

    /// <summary>Rebuilds the reading from the card model and the cards present right now.</summary>
    /// <param name="config">The configuration holding the tracked card libraries.</param>
    /// <param name="presentContentIds">The content ids of the cards attached right now.</param>
    internal static void Update(AppConfig config, IReadOnlySet<string> presentContentIds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(presentContentIds);
        SteamLibraryBadgeState next;
        lock (Gate)
        {
            next = Build(config, presentContentIds, ++_revision);
            _current = next;
        }
        Changed?.Invoke();
    }

    /// <summary>The reading for one card model, without publishing it.</summary>
    /// <param name="config">The configuration holding the tracked card libraries.</param>
    /// <param name="presentContentIds">The content ids of the cards attached right now.</param>
    /// <param name="revision">The revision to stamp.</param>
    /// <returns>Every named card with games, attached or not.</returns>
    internal static SteamLibraryBadgeState Build(
        AppConfig config, IReadOnlySet<string> presentContentIds, long revision = 0)
    {
        IReadOnlyList<SteamLibraryBadgeLibrary> libraries = config.CardLibraries
            .Where(card => !string.IsNullOrWhiteSpace(card.Name) && card.AppIds.Count > 0)
            .Select(card => new SteamLibraryBadgeLibrary(
                card.Name, presentContentIds.Contains(card.ContentId), card.AppIds.ToArray()))
            .ToArray();
        return new SteamLibraryBadgeState(libraries, "Internal", revision);
    }
}

/// <summary>Hears what the library badge reports back: Steam's Big Picture Home layout.</summary>
/// <remarks>
/// The badge sits on the tile and moves with the carousel in either layout, so nothing here
/// changes placement. The report is logged on every transition so a pasted <c>wsgm.log</c> says
/// which layout Steam was in, which is the fact the September 2026 beta made worth knowing.
/// </remarks>
internal sealed class LibraryBadgeBackend : ISteamLibraryBadgeBackend
{
    private bool? _bigArt;

    /// <summary>Whether Steam reported Big Art Mode on, or null before the first report.</summary>
    internal bool? BigArt => _bigArt;

    /// <inheritdoc />
    public Task<SteamUiCommandResult> HomeLayoutAsync(bool bigArt, CancellationToken cancellationToken)
    {
        if (_bigArt != bigArt)
        {
            _bigArt = bigArt;
            Log.Change("steam.home.layout", $"Steam Home layout: Big Art Mode {(bigArt ? "on" : "off")}.");
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }
}
