using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Shell;

/// <summary>Owns the installed boot movies and the selection for Steam and the overlay.</summary>
/// <remarks>
///     Selection is stored separately from content; an empty choice restores Steam's movie. While a WSGM
///     movie is selected, Steam's own startup-movie choice is saved and temporarily cleared after readiness.
///     Override changes take effect on the next Steam start because Steam caches the lookup per document.
///     The repository's listings belong to <see cref="AnimationRepoCatalog" />, and each surface's tab, sort,
///     search and detail to its <see cref="AnimationBrowseSession" />.
/// </remarks>
internal sealed class AnimationService : ISteamAnimationsBackend, IDisposable, IChangeSource
{
    /// <summary>What the choice that puts Big Picture back on Steam's own movie is called.</summary>
    internal const string StockLabel = "Steam's own";

    private const string RestartNote = "Restart Steam to see it.";

    private const string HandBackTooLate =
        "Animations: the shutdown deadline passed before Steam's own Startup Movie choice was given back.";

    private readonly Lock _applyGate = new();
    private readonly AnimationRepoClient _client;

    // Serializes config writes so the file and memory take changes in the same order. Taken before
    // _sync and never while _sync is held, so readers never wait on the config file.
    private readonly Lock _configWrite = new();
    private readonly AnimationLibrary _library;

    // Serializes every library change and reread, its file work included, so _sync only guards what
    // readers see and a slow movie folder never holds up a publication read. Taken before _sync and
    // never while _sync is held.
    private readonly Lock _libraryWork = new();
    private readonly Random _random;
    private readonly Func<AnimationsConfig> _readConfig;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SteamStartupMovieAccess? _steamChoice;
    private readonly Func<string?> _steamDirectory;
    private readonly Func<bool> _steamRunning;
    private readonly Lock _sync = new();

    // The movie operation and every background task this service started and has not seen finish:
    // repository reads, movie downloads and copies, reloads and Steam choice work. The session joins them.
    private readonly ServiceOperation _operation;
    private readonly AnimationRepoCatalog _repository;
    private readonly Action<Action<AnimationsConfig>> _writeConfig;
    private AnimationsConfig _config;
    private bool _disposed;
    private bool _restartNeeded;
    private long _revision;
    private CancellationTokenSource? _steamChoiceWork;

    /// <summary>Creates the service.</summary>
    /// <param name="library">The movies.</param>
    /// <param name="client">The repository.</param>
    /// <param name="readConfig">The current animations configuration.</param>
    /// <param name="writeConfig">Saves a change to the animations configuration.</param>
    /// <param name="steamDirectory">Steam's install directory, or null when Steam is not installed.</param>
    /// <param name="random">The shuffle's source of choice, or null for a fresh one.</param>
    /// <param name="steamRunning">
    ///     Whether Steam runs now, so a change it has already read is announced as needing a restart;
    ///     null counts Steam as running.
    /// </param>
    /// <param name="steamChoice">
    ///     Reaches Steam's own startup movie choice, which replaces the override while it is set; null
    ///     leaves it alone.
    /// </param>
    internal AnimationService(
        AnimationLibrary library,
        AnimationRepoClient client,
        Func<AnimationsConfig> readConfig,
        Action<Action<AnimationsConfig>> writeConfig,
        Func<string?> steamDirectory,
        Random? random = null,
        Func<bool>? steamRunning = null,
        SteamStartupMovieAccess? steamChoice = null)
    {
        _library = library;
        _client = client;
        _readConfig = readConfig;
        _writeConfig = writeConfig;
        _steamDirectory = steamDirectory;
        _random = random ?? new Random();
        _steamRunning = steamRunning ?? (() => true);
        _steamChoice = steamChoice;
        _config = readConfig();
        _operation = new ServiceOperation(_sync);
        _repository = new AnimationRepoCatalog(client, _operation, Publish, _shutdown.Token);
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>Completes when every background task this service started has finished.</summary>
    /// <remarks>The session joins it after <see cref="Dispose" /> cancelled the work, within its deadline.</remarks>
    internal Task Completion => _operation.Completion;

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
        // A Steam choice still waiting is linked to the shutdown and disposes its own source. The
        // source itself stays undisposed: work still finishing reads its token, and it holds no timer.
        _shutdown.Cancel();
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_repository.Refresh());
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        AnimationListing? listing;
        lock (_sync)
        {
            listing = _repository.Find(id) ?? _library.Find(id)?.Listing;
        }

        if (listing is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That movie is not listed."));
        }

        return StartWorkAsync(async token =>
        {
            // The movie goes to a staging file beside its place, and the move into the library runs
            // under the library's own lock, so readers keep reading the state meanwhile.
            var staged = _library.StagingPath(listing.Id);
            await _client.DownloadAsync(listing, staged, token).ConfigureAwait(false);
            string? error;
            lock (_libraryWork)
            {
                error = _library.Adopt(listing, staged);
            }

            if (error is not null)
            {
                throw new AnimationRepoException($"The movie could not be kept: {error}");
            }

            return $"Downloaded {listing.Name}. Choose it under Library to start Big Picture with it.";
        });
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        bool selected;
        string removed;
        AnimationEntry? entry;
        string? error;
        lock (_libraryWork)
        {
            entry = _library.Find(id);
            error = entry is null ? null : _library.Remove(id);
        }

        if (entry is null)
        {
            return new SteamUiCommandResult(false, "That movie is not in the library.");
        }

        lock (_sync)
        {
            if (error is not null)
            {
                return RefuseLocked(error);
            }

            removed = $"Removed {entry.Name}.";
            selected = _config.Boot == id;
            if (!selected)
            {
                SetNoticeLocked(removed);
            }
        }

        if (selected && ChangeConfig(config => config.Boot = string.Empty))
        {
            await ApplyChoiceAsync(removed).ConfigureAwait(false);
            KickSteamChoice();
        }

        Publish();
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (id.Length > 0 && _library.Find(id) is null)
            {
                return RefuseLocked("That movie is not in the library.");
            }

            if (_config.Boot == id)
            {
                return SteamUiCommandResult.Applied;
            }
        }

        return await SetBootAsync(id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ShuffleAsync(CancellationToken cancellationToken)
    {
        string picked;
        lock (_sync)
        {
            if (_library.Entries.Count == 0)
            {
                return RefuseLocked("The library is empty; download a movie first.");
            }

            picked = AnimationShuffle.Pick(_library.Entries, _random);
        }

        return await SetBootAsync(picked).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetShuffleOnStartAsync(bool shuffle, CancellationToken cancellationToken)
    {
        var saved = ChangeConfig(config => config.ShuffleOnStart = shuffle);
        Publish();
        return Task.FromResult(saved ? SteamUiCommandResult.Applied : Unsaved());
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetBootVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        if (!ChangeConfig(config => config.BootVolume = volume))
        {
            Publish();
            return Unsaved();
        }

        await ApplyChoiceAsync(null).ConfigureAwait(false);
        Publish();
        return SteamUiCommandResult.Applied;
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> AddFileAsync(string path, CancellationToken cancellationToken)
    {
        return StartWorkAsync(_ =>
        {
            // The copy and the reread run outside the state lock, so the pages and the overlay keep
            // reading the state while a large movie is copied; the reread holds the library's lock.
            var (id, error) = _library.Import(path);
            if (error is not null)
            {
                throw new AnimationRepoException(error);
            }

            lock (_libraryWork)
            {
                _library.Load();
            }

            string name;
            lock (_sync)
            {
                name = _library.Find(id!)?.Name ?? id!;
            }

            return Task.FromResult($"Added {name}. Choose it under Library to start Big Picture with it.");
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _operation.Clear();
        }

        Publish();
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Reads the library, shuffles when asked to, and writes the override before Steam starts.</summary>
    internal void Start()
    {
        string? picked = null;
        lock (_libraryWork)
        {
            _library.Load();
        }

        lock (_sync)
        {
            if (_config.ShuffleOnStart)
            {
                picked = AnimationShuffle.Pick(_library.Entries, _random);
            }
        }

        // Still synchronous, so the override written below is what Steam reads when it starts.
        if (picked is { } boot)
        {
            if (!ChangeConfig(config => config.Boot = boot))
            {
                Publish();
                return;
            }
        }

        var report = ApplyChoice();
        lock (_sync)
        {
            _operation.Error = report.Error ?? _operation.Error;
        }

        Log.Info($"Animations: {_library.Entries.Count} in the library, boot '{_config.Boot}'"
                 + (report.Changed ? ", override written" : "")
                 + (report.Error is { } error ? $", {error}" : "") + ".");
        Publish();
        KickSteamChoice();
    }

    /// <summary>
    ///     Steam started again, so it has read the override as it stands; its own startup movie choice
    ///     is brought in step once Big Picture is ready.
    /// </summary>
    internal void SteamStarted()
    {
        bool changed;
        lock (_sync)
        {
            changed = _restartNeeded;
            _restartNeeded = false;
        }

        if (changed)
        {
            Publish();
        }

        KickSteamChoice();
    }

    /// <summary>Takes the reloaded configuration.</summary>
    internal void ConfigurationChanged()
    {
        bool apply;
        lock (_sync)
        {
            var previous = _config;
            _config = _readConfig();
            apply = previous.Boot != _config.Boot || previous.BootVolume != _config.BootVolume;
            if (!apply && previous.ShuffleOnStart == _config.ShuffleOnStart)
            {
                return;
            }
        }

        if (!apply)
        {
            Publish();
            return;
        }

        _operation.Track(Task.Run(async () =>
        {
            await ApplyChoiceAsync(null).ConfigureAwait(false);
            Publish();
            KickSteamChoice();
        }));
    }

    /// <summary>Everything the page and the overlay draw, apart from a surface's own browsing.</summary>
    /// <returns>
    ///     The state, with the repository's loading and error but no listing, tab or detail; an
    ///     <see cref="AnimationBrowseSession" /> adds its own.
    /// </returns>
    internal SteamAnimationsState ReadState()
    {
        lock (_sync)
        {
            var steam = _steamDirectory();
            return new SteamAnimationsState(
                string.Empty,
                SelectedLocked(),
                StockLabel,
                [.. _library.Entries.Select(ProjectEntry)],
                _repository.ReadBrowse(),
                null,
                new SteamAnimationsSettings(
                    _config.ShuffleOnStart,
                    _config.BootVolume,
                    _library.Root,
                    steam is null ? null : AnimationOverrides.Directory(steam),
                    _restartNeeded),
                _operation.Busy,
                _operation.Notice,
                _operation.Error ?? _library.LoadError,
                Revision);
        }
    }

    /// <summary>The choice when the library still holds it, else Steam's own. Called under the lock.</summary>
    private string SelectedLocked()
    {
        return _library.Find(_config.Boot) is null ? string.Empty : _config.Boot;
    }

    /// <summary>One movie from the repository or the library, projected now so its library badge is current.</summary>
    /// <param name="id">A listing or library id.</param>
    /// <returns>The movie, or null when neither lists it.</returns>
    internal SteamAnimationsItem? ReadItem(string id)
    {
        lock (_sync)
        {
            if (_repository.Find(id) is { } listing)
            {
                return AnimationRepoCatalog.Project(listing, _library.Find(id) is not null);
            }

            return _library.Find(id) is { } entry ? ProjectEntry(entry) : null;
        }
    }

    /// <summary>Creates one surface's tab, listing and detail over this service.</summary>
    /// <param name="tab">The tab the surface opens on.</param>
    /// <returns>A session the surface owns and disposes.</returns>
    internal AnimationBrowseSession CreateBrowserSession(string tab)
    {
        return new AnimationBrowseSession(this, _repository, tab);
    }

    /// <inheritdoc />
    public string? PreviewPath(string id)
    {
        lock (_sync)
        {
            return _library.Find(id)?.Path;
        }
    }

    private static SteamAnimationsItem ProjectEntry(AnimationEntry entry)
    {
        return entry.Listing is { } listing
            ? AnimationRepoCatalog.Project(listing, true)
            : new SteamAnimationsItem(entry.Id, entry.Name, string.Empty, null, null, string.Empty, 0, 0,
                string.Empty, true, true);
    }

    /// <summary>Makes a library id, or empty for Steam's own, the boot movie and says so.</summary>
    private async Task<SteamUiCommandResult> SetBootAsync(string id)
    {
        // A choice that could not be saved is refused: the previous one stays, in memory and in
        // Steam's override.
        if (!ChangeConfig(config => config.Boot = id))
        {
            Publish();
            return Unsaved();
        }

        await ApplyChoiceAsync(string.Empty).ConfigureAwait(false);
        Publish();
        KickSteamChoice();
        return SteamUiCommandResult.Applied;
    }

    /// <summary>
    ///     Gives Steam's own Startup Movie choice back as WSGM closes: when no WSGM movie is chosen, and
    ///     at uninstall whatever is chosen. One attempt; a choice that cannot be given back stays
    ///     recorded, for the next start or for setup to report.
    /// </summary>
    /// <param name="uninstalling">Whether WSGM is closing for an uninstall.</param>
    /// <param name="deadline">The shutdown's deadline.</param>
    /// <returns>A task that completes once the attempt is done or skipped.</returns>
    /// <remarks>
    ///     With a WSGM movie chosen and WSGM only closing, the override keeps playing that movie, so
    ///     Steam's own choice stays set aside.
    /// </remarks>
    internal async Task HandBackSteamChoiceAsync(bool uninstalling, Deadline deadline)
    {
        if (_steamChoice is not { } access)
        {
            return;
        }

        SteamStartupMovieSetAside? kept;
        bool plays;
        lock (_sync)
        {
            kept = _config.SteamSetAside;
            plays = SelectedLocked().Length > 0;
        }

        if (kept is null || (plays && !uninstalling))
        {
            return;
        }

        if (deadline.HasExpired)
        {
            Log.Warn(HandBackTooLate);
            return;
        }

        SteamStartupMovieResult result;
        using (var budget = deadline.CreateCancellationSource())
        {
            try
            {
                result = await access.Restore(kept.ToChoice(), budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warn(HandBackTooLate);
                return;
            }
        }

        if (!result.Succeeded)
        {
            Log.Warn("Animations: Steam's own Startup Movie choice was not given back as WSGM closed: "
                     + (result.Error ?? "Steam could not be reached."));
            return;
        }

        // Given back, or the user chose anew in Steam since and that choice stays theirs.
        ChangeConfig(config => config.SteamSetAside = null, true);
        Log.Info("Animations: Steam's own Startup Movie choice is back.");
    }

    /// <summary>
    ///     Brings Steam's own startup movie choice in step with WSGM's once Big Picture is ready: set
    ///     aside while one of WSGM's movies plays, given back once Steam's own plays again. A newer
    ///     request supersedes one still waiting.
    /// </summary>
    private void KickSteamChoice()
    {
        if (_steamChoice is not { } access)
        {
            return;
        }

        CancellationTokenSource work;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _steamChoiceWork?.Cancel();
            work = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _steamChoiceWork = work;
        }

        _operation.Track(Task.Run(async () =>
        {
            try
            {
                await access.WhenReady("Boot movie: Steam's startup movie choice",
                    token => ReconcileSteamChoiceAsync(access, token), work.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_steamChoiceWork, work))
                    {
                        _steamChoiceWork = null;
                    }
                }

                work.Dispose();
            }
        }));
    }

    /// <summary>One attempt at <see cref="KickSteamChoice" />.</summary>
    /// <returns>False only when Steam could not be reached, so the attempt is made again.</returns>
    private async Task<bool> ReconcileSteamChoiceAsync(SteamStartupMovieAccess access, CancellationToken token)
    {
        bool plays;
        SteamStartupMovieSetAside? kept;
        lock (_sync)
        {
            plays = SelectedLocked().Length > 0;
            kept = _config.SteamSetAside;
        }

        if (!plays && kept is null)
        {
            return true;
        }

        var result = plays
            ? await access.SetAside(token).ConfigureAwait(false)
            : await access.Restore(kept!.ToChoice(), token).ConfigureAwait(false);
        if (result.Outcome == SteamClientWriteOutcome.NotSent)
        {
            return false;
        }

        string outcome;
        if (!result.Succeeded)
        {
            // A set-aside that failed after its first write already changed Steam's choice, and the
            // reply still names what Steam held: keep it, or the user's movie is lost for good. This
            // is bookkeeping; nothing is written to Steam again on its own. A failed give-back keeps
            // the stored choice as it is.
            if (plays && result.Choice is { } held)
            {
                ChangeConfig(config => config.SteamSetAside = SteamStartupMovieSetAside.From(held), true);
            }

            outcome = $"Steam's own Startup Movie choice could not be {(plays ? "set aside" : "given back")}: "
                      + result.Error;
            lock (_sync)
            {
                _operation.Error = outcome;
            }
        }
        else if (result.Choice is not { } choice)
        {
            // Nothing to set aside, or the user chose anew in Steam since: that choice stays theirs.
            if (!plays)
            {
                ChangeConfig(config => config.SteamSetAside = null, true);
            }

            return true;
        }
        else
        {
            ChangeConfig(config => config.SteamSetAside = plays ? SteamStartupMovieSetAside.From(choice) : null, true);
            lock (_sync)
            {
                _restartNeeded |= _steamRunning();
                outcome = plays
                    ? "Steam's own Startup Movie choice is set aside so WSGM's movie plays."
                    : "Steam's own Startup Movie choice is back.";
                SetNoticeLocked(outcome + (_restartNeeded ? $" {RestartNote}" : string.Empty));
            }
        }

        Log.Info($"Animations: {outcome}");
        Publish();
        return true;
    }

    /// <summary>
    ///     Brings the override in step with the choice off the calling thread, then records whether
    ///     Steam must restart and, with <paramref name="announce" />, says what Big Picture starts with
    ///     after it.
    /// </summary>
    /// <param name="announce">A line to lead the notice with, or null for no notice.</param>
    private async Task ApplyChoiceAsync(string? announce)
    {
        var report = await Task.Run(ApplyChoice).ConfigureAwait(false);
        lock (_sync)
        {
            _restartNeeded |= report.Changed && _steamRunning();
            if (report.Error is not null)
            {
                _operation.Error = report.Error;
            }
            else if (announce is not null || report.Note is not null)
            {
                var name = _library.Find(_config.Boot)?.Name ?? "Steam's own movie";
                var said = announce is null ? string.Empty : $"{announce} Big Picture starts with {name}.";
                SetNoticeLocked($"{said} {report.Note}".Trim()
                                + (_restartNeeded ? $" {RestartNote}" : string.Empty));
            }
        }
    }

    /// <summary>
    ///     Copies the current choice to the override. The copy runs outside the state lock, so a
    ///     reader never waits on it; applies are serialized by their own gate and each takes the
    ///     choice as it stands when it runs, so the last one wins.
    /// </summary>
    private AnimationApplyReport ApplyChoice()
    {
        lock (_applyGate)
        {
            var steam = _steamDirectory();
            if (steam is null)
            {
                return new AnimationApplyReport(false, "Steam is not installed; there is nowhere to write the movie.");
            }

            string? source;
            int volume;
            lock (_sync)
            {
                source = _config.Boot.Length == 0 ? null : _library.Find(_config.Boot)?.Path;
                volume = _config.BootVolume;
            }

            return AnimationOverrides.Apply(AnimationOverrides.Directory(steam), source, volume);
        }
    }

    /// <summary>Writes one change and shows it once saved. Never called under the state lock.</summary>
    /// <param name="change">The change.</param>
    /// <param name="keepUnsaved">
    ///     Keeps the change in memory even when it could not be saved. Only for Steam's own Startup Movie
    ///     choice, which WSGM must not forget while it is set aside.
    /// </param>
    /// <returns>Whether the change was saved.</returns>
    /// <remarks>
    ///     The write takes the cross-process config lock and touches the file, so it runs outside
    ///     <c>_sync</c>: the pages and the overlay keep reading the state while it is saved. A choice whose
    ///     save failed is refused, as the themes refuse one: memory keeps the previous value and the
    ///     caller applies nothing to Steam's override.
    /// </remarks>
    private bool ChangeConfig(Action<AnimationsConfig> change, bool keepUnsaved = false)
    {
        lock (_configWrite)
        {
            string? error = null;
            try
            {
                _writeConfig(change);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or TimeoutException)
            {
                error = $"The choice could not be saved: {ex.Message}";
            }

            lock (_sync)
            {
                if (error is not null)
                {
                    _operation.Error = error;
                }

                if (error is null || keepUnsaved)
                {
                    var next = _config.Clone();
                    change(next);
                    _config = next;
                }
            }

            return error is null;
        }
    }

    private void SetNoticeLocked(string notice)
    {
        _operation.SetNotice(notice);
    }

    private SteamUiCommandResult RefuseLocked(string error)
    {
        _operation.Error = error;
        return new SteamUiCommandResult(false, error);
    }

    /// <summary>The refusal of a choice whose save failed, carrying the save's error.</summary>
    private SteamUiCommandResult Unsaved()
    {
        lock (_sync)
        {
            return new SteamUiCommandResult(false, _operation.Error ?? "The choice could not be saved.");
        }
    }

    /// <summary>Runs download or copy work in the background, answering the command at once.</summary>
    private Task<SteamUiCommandResult> StartWorkAsync(Func<CancellationToken, Task<string>> work)
    {
        return Task.FromResult(_operation.Start(
            "Another movie is still being fetched.",
            () => work(_shutdown.Token),
            static ex =>
            {
                if (ex is AnimationRepoException)
                {
                    return ex.Message;
                }

                Log.Warn($"Animations: movie operation failed: {ex}");
                return "The movie operation could not be completed. See wsgm.log for details.";
            },
            Publish,
            (notice, error) =>
            {
                Log.Info($"Animations: {notice ?? error}");
                Publish();
            }));
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
