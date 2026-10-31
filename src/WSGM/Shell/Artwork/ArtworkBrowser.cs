using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The shared half of the artwork browser: providers, saved state, Steam and every surface's session.</summary>
/// <remarks>
///     The Steam Change Artwork page and each overlay view browse through their own
///     <see cref="SteamArtworkBrowserSource" />, created here. This owner resets the providers' caches when the
///     credentials change, reopens each session's game when the tab layout or credentials change, and tells the
///     other sessions on the same game what one session applied. Every session writes through the one
///     <see cref="Writer" /> and looks official artwork up through the shared SteamGridDB provider here.
/// </remarks>
internal sealed class ArtworkBrowser : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SteamArtworkBrowserSource> _sessions = [];

    /// <summary>The SteamGridDB provider among <see cref="Providers" />, which alone knows official artwork.</summary>
    private readonly SteamGridDbProvider? _steamGridDb;

    private bool _disposed;
    private string _providerSignature;
    private string _tabSignature;

    /// <summary>Creates the owner.</summary>
    /// <param name="readConfiguration">Reads the current artwork configuration.</param>
    /// <param name="store">Saved game matches and filters.</param>
    /// <param name="steam">The Steam client artwork is read from and written to.</param>
    /// <param name="providers">The artwork providers, or null for the built-in ones.</param>
    internal ArtworkBrowser(
        Func<ArtworkConfig> readConfiguration,
        ArtworkStateStore store, SteamClient steam, IReadOnlyList<IArtworkProvider>? providers = null)
    {
        ReadConfiguration = readConfiguration;
        Store = store;
        Steam = steam ?? throw new ArgumentNullException(nameof(steam));
        Providers = providers ?? ArtworkSearch.Providers;
        _steamGridDb = Providers.OfType<SteamGridDbProvider>().FirstOrDefault();
        Writer = new SteamArtworkWriter(Steam, readConfiguration);
        var configuration = readConfiguration();
        _tabSignature = configuration.TabSignature();
        _providerSignature = configuration.ProviderSignature();
    }

    /// <summary>Reads the current artwork configuration.</summary>
    internal Func<ArtworkConfig> ReadConfiguration { get; }

    /// <summary>Saved game matches and filters.</summary>
    internal ArtworkStateStore Store { get; }

    /// <summary>The Steam client artwork is read from and written to.</summary>
    internal SteamClient Steam { get; }

    /// <summary>The artwork providers every session searches.</summary>
    internal IReadOnlyList<IArtworkProvider> Providers { get; }

    /// <summary>Writes artwork and logo placement to <see cref="Steam" /> for every session.</summary>
    internal SteamArtworkWriter Writer { get; }

    /// <summary>Disposes every session still open.</summary>
    public void Dispose()
    {
        SteamArtworkBrowserSource[] sessions;
        lock (_gate)
        {
            _disposed = true;
            sessions = [.. _sessions];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    /// <summary>Creates one surface's browsing.</summary>
    /// <returns>A session the surface owns and disposes.</returns>
    internal SteamArtworkBrowserSource CreateViewSession()
    {
        var session = new SteamArtworkBrowserSource(this);
        lock (_gate)
        {
            if (!_disposed)
            {
                _sessions.Add(session);
            }
        }

        return session;
    }

    /// <summary>Forgets a session that was disposed.</summary>
    /// <param name="session">The session.</param>
    internal void Remove(SteamArtworkBrowserSource session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }

    /// <summary>Takes the reloaded configuration: new credentials reset the caches, and every session reopens.</summary>
    internal void ConfigurationChanged()
    {
        var configuration = ReadConfiguration();
        var tabSignature = configuration.TabSignature();
        var providerSignature = configuration.ProviderSignature();
        SteamArtworkBrowserSource[] sessions;
        bool credentialsChanged;
        lock (_gate)
        {
            if (_tabSignature == tabSignature && _providerSignature == providerSignature)
            {
                return;
            }

            _tabSignature = tabSignature;
            credentialsChanged = _providerSignature != providerSignature;
            _providerSignature = providerSignature;
            sessions = [.. _sessions];
        }

        if (credentialsChanged)
        {
            foreach (var provider in Providers)
            {
                provider.ResetCache();
            }
        }

        foreach (var session in sessions)
        {
            session.Reopen();
        }
    }

    /// <summary>Shows the other sessions on the same game what one session applied.</summary>
    /// <param name="from">The session that applied it, which shows its own outcome.</param>
    /// <param name="appId">The game.</param>
    /// <param name="message">The outcome.</param>
    /// <param name="managed">The game's managed slots now.</param>
    internal void BroadcastArtwork(SteamArtworkBrowserSource from, uint appId, string message,
        SteamArtworkManagedSlot[] managed)
    {
        SteamArtworkBrowserSource[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Where(session => !ReferenceEquals(session, from))];
        }

        foreach (var session in sessions)
        {
            session.ShowArtwork(appId, message, managed);
        }
    }

    /// <summary>Looks up a slot's official artwork for a game on SteamGridDB.</summary>
    /// <param name="asset">The slot.</param>
    /// <param name="appId">The game's Steam app id.</param>
    /// <param name="selected">The game match the session uses, or null for the Steam app id.</param>
    /// <param name="config">The current artwork configuration, which carries the SteamGridDB key.</param>
    /// <param name="cancellationToken">Ends the lookup.</param>
    /// <returns>
    ///     The official images, or none without a key, for a shortcut that has no SteamGridDB match, or when the
    ///     lookup failed.
    /// </returns>
    internal async Task<IReadOnlyList<SgdbOfficialAsset>> LoadOfficialAssetsAsync(
        ArtworkAsset asset,
        uint appId,
        ArtworkGameMatch? selected,
        ArtworkConfig config,
        CancellationToken cancellationToken)
    {
        var key = SteamGridDbProvider.ResolveKey(config);
        if (key.Length == 0 || _steamGridDb is null)
        {
            return [];
        }

        try
        {
            if (selected is { ProviderId: "steamgriddb" }
                && int.TryParse(selected.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId))
            {
                return await _steamGridDb.GetOfficialAssetsForGameAsync(asset, gameId, key, cancellationToken)
                    .ConfigureAwait(false);
            }

            return SteamApps.IsShortcutAppId(appId)
                ? []
                : await _steamGridDb.GetOfficialAssetsForSteamAppAsync(asset, appId, key, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Steam artwork page: official artwork lookup failed: {ex.Message}");
            return [];
        }
    }
}
