using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The boot-movie repository's listings as the session last read them, and the read in progress.</summary>
/// <remarks>
///     The list is read once on first use and again on an explicit refresh. A newer read supersedes one still
///     answering, so a slow reply never replaces a fresher list. The list instance stays the same until a read
///     replaces it, which lets a browse session keep its sorted order until then.
/// </remarks>
internal sealed class AnimationRepoCatalog
{
    private readonly AnimationRepoClient _client;
    private readonly Lock _gate = new();
    private readonly ServiceOperation _operation;
    private readonly Action _publish;
    private readonly CancellationToken _shutdown;
    private string? _error;
    private IReadOnlyList<AnimationListing>? _listings;
    private bool _loading;
    private int _sequence;

    /// <summary>Creates the catalog; nothing is read until it is first used.</summary>
    /// <param name="client">The repository.</param>
    /// <param name="operation">The owner's operation, which joins every read for the session shutdown.</param>
    /// <param name="publish">Publishes a change of the list, its loading state or its error.</param>
    /// <param name="shutdown">The owner's shutdown; it cancels a read in progress.</param>
    internal AnimationRepoCatalog(AnimationRepoClient client, ServiceOperation operation, Action publish,
        CancellationToken shutdown)
    {
        _client = client;
        _operation = operation;
        _publish = publish;
        _shutdown = shutdown;
    }

    /// <summary>The repository's listings, the same instance until a read replaces them; empty before one.</summary>
    internal IReadOnlyList<AnimationListing> Listings
    {
        get
        {
            lock (_gate)
            {
                return _listings ?? [];
            }
        }
    }

    /// <summary>The listing summary a snapshot carries before a browse session adds its own page.</summary>
    /// <returns>The default sort, no items, the listing count, the loading state and the last read's error.</returns>
    internal SteamAnimationsBrowse ReadBrowse()
    {
        lock (_gate)
        {
            return new SteamAnimationsBrowse(
                SteamAnimationsSurface.Sorts[0].Id,
                SteamAnimationsSurface.Sorts,
                string.Empty,
                [],
                0,
                _listings?.Count ?? 0,
                _loading,
                _error);
        }
    }

    /// <summary>One listing by id.</summary>
    /// <param name="id">The listing id.</param>
    /// <returns>The listing, or null when the last read did not list it.</returns>
    internal AnimationListing? Find(string id)
    {
        lock (_gate)
        {
            return _listings?.FirstOrDefault(candidate => candidate.Id == id);
        }
    }

    /// <summary>Reads the list again, unless a read is still answering.</summary>
    /// <returns>Applied once the read started, or refused while another is running.</returns>
    internal SteamUiCommandResult Refresh()
    {
        int sequence;
        lock (_gate)
        {
            if (_loading)
            {
                return new SteamUiCommandResult(false, "The repository is still answering.");
            }

            sequence = Begin();
        }

        _publish();
        _operation.Track(Task.Run(() => FetchAsync(sequence, _shutdown)));
        return SteamUiCommandResult.Applied;
    }

    /// <summary>Reads the list when it was never read and no read is running.</summary>
    internal void Ensure()
    {
        int sequence;
        lock (_gate)
        {
            if (_listings is not null || _loading)
            {
                return;
            }

            sequence = Begin();
        }

        _publish();
        _operation.Track(Task.Run(() => FetchAsync(sequence, _shutdown)));
    }

    /// <summary>A listing's update time, read as UTC when it names no offset.</summary>
    /// <param name="updated">The repository's date text.</param>
    /// <returns>The time, or null when the text is not a date.</returns>
    internal static DateTimeOffset? When(string updated)
    {
        return DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var when)
            ? when
            : null;
    }

    /// <summary>Projects one repository listing.</summary>
    /// <param name="listing">The listing.</param>
    /// <param name="downloaded">Whether the library holds it.</param>
    /// <returns>The movie as the pages draw it.</returns>
    internal static SteamAnimationsItem Project(AnimationListing listing, bool downloaded)
    {
        return new SteamAnimationsItem(
            listing.Id,
            listing.Name,
            listing.Author,
            listing.ThumbnailUrl.Length > 0 ? listing.ThumbnailUrl : null,
            listing.PreviewUrl.Length > 0 ? listing.PreviewUrl : null,
            listing.Description,
            listing.Likes,
            listing.Downloads,
            When(listing.Updated)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? listing.Updated,
            downloaded,
            false);
    }

    /// <summary>Marks a read as running and answers its sequence. Called under the gate.</summary>
    private int Begin()
    {
        _loading = true;
        _error = null;
        return ++_sequence;
    }

    private async Task FetchAsync(int sequence, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnimationListing>? list = null;
        string? error = null;
        try
        {
            list = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AnimationRepoException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (sequence != _sequence)
            {
                return;
            }

            _loading = false;
            _error = error;
            if (list is not null)
            {
                _listings = list;
            }
        }

        Log.Change("animations.repository", error ?? $"{list!.Count} listed");
        _publish();
    }
}
