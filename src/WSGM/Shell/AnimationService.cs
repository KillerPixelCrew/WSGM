using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The boot movies' one owner: the repository's list, WSGM's library, which movie Big Picture
///     starts with, and the override file that makes it so. The Animations page in Steam, the Quick
///     Access section and the overlay's Animations view all read its state and call its methods.
/// </summary>
/// <remarks>
///     <para>
///         Content and choice stay apart, as Animation Changer keeps them: the library holds the
///         movies, the configuration names one, and applying copies it to the file the client asks
///         for (<see cref="AnimationOverrides" />). A shuffle picks anew from the library and an empty
///         choice is Steam's own movie.
///     </para>
///     <para>
///         The client caches its override lookup for the life of the document, so an override written
///         while Steam runs shows at the next Steam start; the state says so until then. Only the boot
///         movie is offered: nothing on Windows drives Steam's suspend flow, so its suspend movies
///         never play (#116, #21).
///     </para>
/// </remarks>
internal sealed class AnimationService : ISteamAnimationsBackend, IDisposable, IChangeSource
{
    /// <summary>The section's item id.</summary>
    internal const string ExtensionsId = "wsgm.animations";

    /// <summary>The action that opens the Browse tab.</summary>
    internal const string ExtensionsBrowseId = "wsgm.animations.browse";

    /// <summary>The action that opens the Library tab.</summary>
    internal const string ExtensionsManageId = "wsgm.animations.manage";

    /// <summary>The action that picks the boot movie anew.</summary>
    internal const string ExtensionsShuffleId = "wsgm.animations.shuffle";

    /// <summary>The choice that puts Big Picture back on Steam's own movie.</summary>
    internal const string StockLabel = "Steam's own";

    private const string RestartNote = "Restart Steam to see it.";

    private readonly AnimationRepoClient _client;
    private readonly AnimationLibrary _library;
    private readonly Random _random;
    private readonly Func<AnimationsConfig> _readConfig;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string?> _steamDirectory;
    private readonly Lock _sync = new();
    private readonly Action<Action<AnimationsConfig>> _writeConfig;
    private string _activeTab = "browse";
    private string _browseSearch = string.Empty;
    private string _browseSort = "Newest";
    private bool _busy;
    private AnimationsConfig _config;
    private SteamAnimationsItem? _detail;
    private bool _disposed;
    private string? _error;
    private string? _notice;
    private IReadOnlyList<AnimationListing>? _repo;
    private string? _repoError;
    private DateTimeOffset? _repoFetched;
    private bool _repoLoading;
    private int _repoSequence;
    private bool _restartNeeded;
    private long _revision;

    /// <summary>Creates the service.</summary>
    /// <param name="library">The movies.</param>
    /// <param name="client">The repository.</param>
    /// <param name="readConfig">The current animations configuration.</param>
    /// <param name="writeConfig">Saves a change to the animations configuration.</param>
    /// <param name="steamDirectory">Steam's install directory, or null when Steam is not installed.</param>
    /// <param name="random">The shuffle's source of choice, or null for a fresh one.</param>
    internal AnimationService(
        AnimationLibrary library,
        AnimationRepoClient client,
        Func<AnimationsConfig> readConfig,
        Action<Action<AnimationsConfig>> writeConfig,
        Func<string?> steamDirectory,
        Random? random = null)
    {
        _library = library;
        _client = client;
        _readConfig = readConfig;
        _writeConfig = writeConfig;
        _steamDirectory = steamDirectory;
        _random = random ?? new Random();
        _config = readConfig();
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>Raised on every change the page, the section or the overlay should draw.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _activeTab = tab;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseAsync(string sort, string search, CancellationToken cancellationToken)
    {
        var fetch = false;
        int sequence;
        lock (_sync)
        {
            _browseSort = SteamAnimationsSurface.Sorts.Contains(sort) ? sort : SteamAnimationsSurface.Sorts[0];
            _browseSearch = search.Trim();
            if (_repo is null && !_repoLoading)
            {
                _repoLoading = true;
                _repoError = null;
                fetch = true;
            }

            sequence = ++_repoSequence;
        }

        Publish();
        if (fetch)
        {
            _ = Task.Run(() => FetchRepoAsync(sequence, _shutdown.Token));
        }

        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        int sequence;
        lock (_sync)
        {
            if (_repoLoading)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "The repository is still answering."));
            }

            _repoLoading = true;
            _repoError = null;
            sequence = ++_repoSequence;
        }

        Publish();
        _ = Task.Run(() => FetchRepoAsync(sequence, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var listing = _repo?.FirstOrDefault(candidate => candidate.Id == id);
            var entry = _library.Find(id);
            if (listing is null && entry is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That movie is not listed."));
            }

            _detail = listing is not null ? ProjectListing(listing) : ProjectEntry(entry!);
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _detail = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        AnimationListing? listing;
        lock (_sync)
        {
            listing = _repo?.FirstOrDefault(candidate => candidate.Id == id) ?? _library.Find(id)?.Listing;
        }

        if (listing is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That movie is not listed."));
        }

        return StartWorkAsync(async token =>
        {
            using var movie = await _client.DownloadAsync(listing, token).ConfigureAwait(false);
            string? error;
            lock (_sync)
            {
                error = _library.Add(listing, movie);
                RefreshDetailLocked();
            }

            if (error is not null)
            {
                throw new AnimationRepoException($"The movie could not be kept: {error}");
            }

            return $"Downloaded {listing.Name}. Choose it under Library to start Big Picture with it.";
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var entry = _library.Find(id);
            if (entry is null)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "That movie is not in the library."));
            }

            var error = _library.Remove(id);
            if (error is not null)
            {
                return Task.FromResult(RefuseLocked(error));
            }

            var playing = _config.Boot == id;
            if (playing)
            {
                ChangeConfigLocked(config => config.Boot = string.Empty);
                _restartNeeded |= ApplyLocked().Changed;
            }

            RefreshDetailLocked();
            SetNoticeLocked(playing
                ? $"Removed {entry.Name}; Big Picture starts with Steam's own movie again. {RestartNote}"
                : $"Removed {entry.Name}.");
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (id.Length > 0 && _library.Find(id) is null)
            {
                return Task.FromResult(RefuseLocked("That movie is not in the library."));
            }

            if (_config.Boot == id)
            {
                return Task.FromResult(SteamUiCommandResult.Applied);
            }

            ChangeConfigLocked(config => config.Boot = id);
            var report = ApplyLocked();
            _restartNeeded |= report.Changed;
            if (report.Error is null)
            {
                SetNoticeLocked(
                    $"Big Picture starts with {(id.Length == 0 ? "Steam's own movie" : _library.Find(id)!.Name)}. {RestartNote}");
            }
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_library.Entries.Count == 0)
            {
                return Task.FromResult(RefuseLocked("The library is empty; download a movie first."));
            }

            var picked = AnimationShuffle.Pick(_library.Entries, _config.ShuffleExclusions, _random);
            ChangeConfigLocked(config => config.Boot = picked);
            _restartNeeded |= ApplyLocked().Changed;
            SetNoticeLocked(
                $"Big Picture starts with {_library.Find(picked)?.Name ?? "Steam's own movie"}. {RestartNote}");
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        if (key != "shuffleOnStart" || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That setting is not one of the animations'."));
        }

        var shuffle = value.GetBoolean();
        lock (_sync)
        {
            ChangeConfigLocked(config => config.ShuffleOnStart = shuffle);
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken)
    {
        return StartWorkAsync(_ =>
        {
            string? id;
            string? error;
            lock (_sync)
            {
                (id, error) = _library.Import(path);
            }

            if (error is not null)
            {
                throw new AnimationRepoException(error);
            }

            var name = _library.Find(id!)?.Name ?? id!;
            return Task.FromResult($"Added {name}. Choose it under Library to start Big Picture with it.");
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _notice = null;
            _error = null;
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Reads the library, shuffles when asked to, and writes the override before Steam starts.</summary>
    internal void Start()
    {
        AnimationApplyReport report;
        lock (_sync)
        {
            _library.Load();
            if (_config.ShuffleOnStart)
            {
                var picked = AnimationShuffle.Pick(_library.Entries, _config.ShuffleExclusions, _random);
                ChangeConfigLocked(config => config.Boot = picked);
            }

            report = ApplyLocked();
        }

        Log.Info($"Animations: {_library.Entries.Count} in the library, boot '{_config.Boot}'"
                 + (report.Changed ? ", override written" : "")
                 + (report.Error is { } error ? $", {error}" : "") + ".");
        Publish();
    }

    /// <summary>Takes the reloaded configuration.</summary>
    internal void ConfigurationChanged()
    {
        var changed = false;
        lock (_sync)
        {
            var previous = _config;
            _config = _readConfig();
            if (previous.Boot != _config.Boot)
            {
                _restartNeeded |= ApplyLocked().Changed;
                changed = true;
            }
            else if (previous.ShuffleOnStart != _config.ShuffleOnStart
                     || !previous.ShuffleExclusions.SequenceEqual(_config.ShuffleExclusions, StringComparer.Ordinal))
            {
                changed = true;
            }
        }

        if (changed)
        {
            Publish();
        }
    }

    /// <summary>Everything the page, the section and the overlay draw.</summary>
    /// <returns>The state.</returns>
    internal SteamAnimationsState ReadState()
    {
        lock (_sync)
        {
            var browse = new SteamAnimationsBrowse(
                _browseSort,
                _browseSearch,
                _repo is null ? [] : Filtered(_repo).Select(ProjectListing).ToList(),
                _repo?.Count ?? 0,
                _repoLoading,
                _repoError,
                _repoFetched?.ToString("u", CultureInfo.InvariantCulture));
            var steam = _steamDirectory();
            var settings = new SteamAnimationsSettings(
                _config.ShuffleOnStart,
                _library.Root,
                steam is null ? null : AnimationOverrides.Directory(steam),
                _restartNeeded);
            return new SteamAnimationsState(
                _activeTab,
                _library.Find(_config.Boot) is null ? string.Empty : _config.Boot,
                [.. _library.Entries.Select(ProjectEntry)],
                browse,
                _detail,
                settings,
                _busy,
                _notice,
                _error ?? _library.LoadError,
                _revision);
        }
    }

    /// <summary>The Quick Access section: the boot choice, the shuffle, and the way to the page.</summary>
    /// <returns>The item.</returns>
    internal SteamExtensionsTabItem ReadExtensionsItem()
    {
        var state = ReadState();
        var selected = state.Library.FirstOrDefault(item => item.Id == state.Selected);
        List<SteamExtensionsTabSetting> settings =
        [
            new("boot", "Boot movie", "text", TextValue: selected?.Name ?? StockLabel,
                Choices: [StockLabel, .. state.Library.Select(item => item.Name)]),
            new("shuffleOnStart", "Shuffle on start", "boolean", state.Settings.ShuffleOnStart,
                Description: "Picks the boot movie anew from the library each time WSGM starts.")
        ];
        var detail = state.Library.Count == 0 ? "No movies yet" : $"{state.Library.Count} in the library";
        if (state.Settings.RestartNeeded)
        {
            detail += " · Restart Steam to see the change";
        }

        return new SteamExtensionsTabItem(
            ExtensionsId,
            "Boot animation",
            string.Empty,
            state.Busy ? "Working…" : "Ready",
            detail,
            [
                new SteamExtensionsTabAction(ExtensionsBrowseId, "Browse movies…"),
                new SteamExtensionsTabAction(ExtensionsManageId, "Library…"),
                new SteamExtensionsTabAction(ExtensionsShuffleId, "Shuffle")
            ],
            settings,
            state.Revision);
    }

    /// <summary>Answers one of the section's actions.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result, carrying the page's route for the two that open it.</returns>
    internal async Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        switch (id)
        {
            case ExtensionsBrowseId:
                await SetTabAsync("browse", cancellationToken).ConfigureAwait(false);
                return RouteAnswer();
            case ExtensionsManageId:
                await SetTabAsync("library", cancellationToken).ConfigureAwait(false);
                return RouteAnswer();
            case ExtensionsShuffleId:
                return await ShuffleAsync(cancellationToken).ConfigureAwait(false);
            default:
                return new SteamUiCommandResult(false, "That entry is no longer available.");
        }
    }

    /// <summary>Applies one of the section's settings.</summary>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">Its new value.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    internal Task<SteamUiCommandResult> ConfigureExtensionAsync(
        string key, JsonElement value, CancellationToken cancellationToken)
    {
        if (key == "shuffleOnStart")
        {
            return SetSettingAsync(key, value, cancellationToken);
        }

        if (key != "boot" || value.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."));
        }

        var chosen = value.GetString() ?? string.Empty;
        string? id;
        lock (_sync)
        {
            id = chosen == StockLabel
                ? string.Empty
                : _library.Entries.FirstOrDefault(entry => entry.Name == chosen)?.Id;
        }

        return id is null
            ? Task.FromResult(new SteamUiCommandResult(false, "That movie is no longer in the library."))
            : SelectAsync(id, cancellationToken);
    }

    private async Task FetchRepoAsync(int sequence, CancellationToken cancellationToken)
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

        lock (_sync)
        {
            if (sequence != _repoSequence)
            {
                return;
            }

            _repoLoading = false;
            if (list is not null)
            {
                _repo = list;
                _repoFetched = DateTimeOffset.UtcNow;
                _repoError = null;
            }
            else
            {
                _repoError = error;
            }
        }

        Log.Change("animations.repository", error ?? $"{list!.Count} listed");
        Publish();
    }

    private IEnumerable<AnimationListing> Filtered(IReadOnlyList<AnimationListing> repo)
    {
        IEnumerable<AnimationListing> items = repo;
        if (_browseSearch.Length > 0)
        {
            items = items.Where(listing => listing.Name.Contains(_browseSearch, StringComparison.OrdinalIgnoreCase));
        }

        return _browseSort switch
        {
            "Oldest" => items.OrderBy(listing => When(listing.Updated)),
            "Alphabetical" => items.OrderBy(listing => listing.Name, StringComparer.OrdinalIgnoreCase),
            "Most popular" => items.OrderByDescending(listing => listing.Downloads),
            "Most liked" => items.OrderByDescending(listing => listing.Likes),
            _ => items.OrderByDescending(listing => When(listing.Updated))
        };
    }

    private static DateTimeOffset When(string updated)
    {
        return DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var when)
            ? when
            : DateTimeOffset.MinValue;
    }

    private SteamAnimationsItem ProjectListing(AnimationListing listing)
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
            When(listing.Updated) == DateTimeOffset.MinValue
                ? listing.Updated
                : When(listing.Updated).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _library.Find(listing.Id) is not null,
            false);
    }

    private SteamAnimationsItem ProjectEntry(AnimationEntry entry)
    {
        return entry.Listing is { } listing
            ? ProjectListing(listing) with { Downloaded = true }
            : new SteamAnimationsItem(entry.Id, entry.Name, string.Empty, null, null, string.Empty, 0, 0,
                string.Empty, true, true);
    }

    /// <summary>Brings the override in step with the choice. Called under the lock.</summary>
    private AnimationApplyReport ApplyLocked()
    {
        var steam = _steamDirectory();
        if (steam is null)
        {
            return new AnimationApplyReport(false, "Steam is not installed; there is nowhere to write the movie.");
        }

        var source = _config.Boot.Length == 0 ? null : _library.Find(_config.Boot)?.Path;
        var report = AnimationOverrides.Apply(AnimationOverrides.Directory(steam), source);
        _error = report.Error;
        return report;
    }

    /// <summary>Writes one change and shows it at once. Called under the lock.</summary>
    private void ChangeConfigLocked(Action<AnimationsConfig> change)
    {
        try
        {
            _writeConfig(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _error = $"The choice could not be saved: {ex.Message}";
        }

        var next = _config.Clone();
        change(next);
        _config = next;
    }

    private void RefreshDetailLocked()
    {
        if (_detail is { } detail)
        {
            _detail = detail with { Downloaded = _library.Find(detail.Id) is not null };
        }
    }

    private void SetNoticeLocked(string notice)
    {
        _notice = notice;
        _error = null;
    }

    private SteamUiCommandResult RefuseLocked(string error)
    {
        _error = error;
        return new SteamUiCommandResult(false, error);
    }

    private static SteamUiCommandResult RouteAnswer()
    {
        return new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["route"] = SteamAnimationsSurface.Route }));
    }

    /// <summary>Runs download or copy work in the background, answering the command at once.</summary>
    private Task<SteamUiCommandResult> StartWorkAsync(Func<CancellationToken, Task<string>> work)
    {
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another movie is still being fetched."));
            }

            _busy = true;
            _error = null;
            _notice = null;
        }

        Publish();
        _ = Task.Run(async () =>
        {
            string? notice = null;
            string? error = null;
            try
            {
                notice = await work(_shutdown.Token).ConfigureAwait(false);
            }
            catch (AnimationRepoException ex)
            {
                error = ex.Message;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_sync)
            {
                _busy = false;
                _notice = notice;
                _error = error;
            }

            Log.Info($"Animations: {notice ?? error}");
            Publish();
        });
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private void Publish()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }
}
