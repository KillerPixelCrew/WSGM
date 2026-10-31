using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Owns CSSLoader-compatible theme installation and active styles for Steam and the overlay.</summary>
/// <remarks>
///     Folder-only commands await completion. Store requests run in the background and publish progress
///     through <see cref="Changed" />; failures await an explicit retry. The toolkit consumes the revisioned
///     cascade from <see cref="ReadStyles" />. Store browsing belongs to each surface's
///     <see cref="ThemeBrowseSession" />, and which installed themes have a newer store version to
///     <see cref="ThemeUpdateCheck" />.
/// </remarks>
internal sealed class ThemeService : ISteamThemesBackend, IDisposable, IChangeSource
{
    // The selected-preset text while more than one preset is enabled. CSS Loader's own wording.
    private const string MultiplePresetsSelected = "Invalid State";

    /// <summary>The listing a snapshot carries before a browse session adds its own.</summary>
    private static readonly SteamThemesBrowse EmptyBrowse = new(
        ThemeStoreQuery.AllFilter,
        ThemeStoreQuery.DefaultOrder,
        string.Empty,
        new Dictionary<string, int>(StringComparer.Ordinal),
        [ThemeStoreQuery.DefaultOrder],
        [],
        0,
        0,
        false,
        null);

    private readonly ThemeStoreClient _client;

    // Serializes config writes so the file and memory take changes in the same order. Taken before
    // _sync and never while _sync is held.
    private readonly Lock _configWrite = new();
    private readonly ThemeInstaller _installer;
    private readonly ThemeLoader _loader;

    // Serializes every loader call, its file reads and writes included, so _sync only guards what
    // readers see and a slow themes folder never holds up a publication read. Taken before _sync and
    // never while _sync is held.
    private readonly Lock _loaderWork = new();
    private readonly Func<ThemesConfig> _readConfig;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string?> _steamDirectory;
    private readonly Lock _sync = new();

    // The store operation and every background task this service started and has not seen finish:
    // browse sessions' store reads, installs, the junction, the translations loop and update checks. The
    // session joins them.
    private readonly ServiceOperation _operation;
    private readonly ThemeTranslations _translations;
    private readonly ThemeUpdateCheck _updates;
    private readonly Action<Action<ThemesConfig>> _writeConfig;
    private ThemesConfig _config;
    private bool _disposed;

    // What the loader held after its last change, published under _sync for every reader.
    private IReadOnlyList<ThemeSnapshot> _installed = [];
    private IReadOnlyList<ThemeLoadError> _loadErrors = [];
    private long _revision;
    private bool _steamBeta;
    private string _steamLink = "Not checked yet.";
    private SteamThemeState? _styles;
    private bool _stylesDirty = true;
    private long _stylesRevision;

    /// <summary>Creates the service.</summary>
    /// <param name="loader">The installed themes.</param>
    /// <param name="client">The store.</param>
    /// <param name="readConfig">The current themes configuration.</param>
    /// <param name="writeConfig">Saves a change to the themes configuration, or null when nothing can be saved.</param>
    /// <param name="steamDirectory">Steam's install directory, or null when Steam is not installed.</param>
    internal ThemeService(
        ThemeLoader loader,
        ThemeStoreClient client,
        Func<ThemesConfig> readConfig,
        Action<Action<ThemesConfig>> writeConfig,
        Func<string?> steamDirectory)
    {
        _loader = loader;
        _client = client;
        _installer = new ThemeInstaller(client, loader.Root);
        _readConfig = readConfig;
        _writeConfig = writeConfig;
        _steamDirectory = steamDirectory;
        _config = readConfig();
        _operation = new ServiceOperation(_sync);
        _translations = new ThemeTranslations(client, loader.Root, WantsBetaTranslations, ApplyTranslations);
        _updates = new ThemeUpdateCheck(client, () => Publish(false));
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>The revision of the cascade <see cref="ReadStyles" /> answers.</summary>
    internal long StylesRevision => Interlocked.Read(ref _stylesRevision);

    /// <summary>Whether enabled themes are installed into Steam.</summary>
    internal bool Enabled => ReadConfig().Enabled;

    /// <summary>The themes folder.</summary>
    internal string Root => _loader.Root;

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
        // The source stays undisposed: work still finishing reads its token, and it holds no timer.
        _shutdown.Cancel();
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> InstallAsync(string id, CancellationToken cancellationToken)
    {
        return StartWorkAsync(async token =>
        {
            IReadOnlyCollection<string> local;
            lock (_sync)
            {
                local = [.. _installed.Select(theme => theme.Name)];
            }

            var installed = await _installer.InstallAsync(id, local, token).ConfigureAwait(false);
            return installed.Count == 1
                ? $"Installed {installed[0]}. Turn it on under Installed."
                : $"Installed {installed[0]} and {installed.Count - 1} it needs. Turn it on under Installed.";
        }, true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UpdateAsync(string name, CancellationToken cancellationToken)
    {
        var id = _updates.OutdatedId(name);
        if (id is null)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "The store has no newer version of that theme."));
        }

        return StartWorkAsync(async token =>
        {
            await UpdateOneAsync(name, id, token).ConfigureAwait(false);
            return $"Updated {name}.";
        }, true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> UpdateAllAsync(CancellationToken cancellationToken)
    {
        var outdated = _updates.Outdated();
        if (outdated.Count == 0)
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Every installed theme is current."));
        }

        return StartWorkAsync(async token =>
        {
            foreach (var (name, id) in outdated)
            {
                await UpdateOneAsync(name, id, token).ConfigureAwait(false);
            }

            return $"Updated {outdated.Count} theme{(outdated.Count == 1 ? "" : "s")}.";
        }, true);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        return LoaderCommand(() =>
        {
            var error = _loader.DeleteTheme(name);
            _updates.Forget(name);
            return error;
        }, $"Deleted {name}.");
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        return LoaderCommand(() => _loader.SetThemeState(name, enabled));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetPatchAsync(
        string theme, string patch, string value, CancellationToken cancellationToken)
    {
        return LoaderCommand(() => _loader.SetPatch(theme, patch, value));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetComponentAsync(
        string theme, string patch, string component, string value, CancellationToken cancellationToken)
    {
        return LoaderCommand(() => _loader.SetComponent(theme, patch, component, value));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetProfileAsync(string name, CancellationToken cancellationToken)
    {
        return LoaderCommand(() =>
        {
            // The profile on now goes off first, with the themes only it turned on; then the chosen
            // one comes on with its own, as CSS Loader's profile dropdown does it.
            string? error = null;
            foreach (var preset in _loader.Themes.Where(theme => theme.IsPreset && theme.Enabled && theme.Name != name))
            {
                error ??= _loader.SetThemeState(preset.Name, false);
            }

            if (name.Length > 0)
            {
                error ??= _loader.SetThemeState(name, true);
            }

            return error;
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CreateProfileAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "Please add a name to your profile."));
        }

        string? error;
        int combined;
        lock (_loaderWork)
        {
            if (IsBusy())
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            combined = _loader.Themes.Count(theme => theme.Enabled && !theme.IsPreset);
            error = _loader.GeneratePreset(name.Trim());
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        // The folder now holds a theme the loader has not read.
        Reload();
        SetNotice($"Profile {name.Trim()} combines {combined} theme{(combined == 1 ? "" : "s")}.");
        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> RefreshAsync(CancellationToken cancellationToken)
    {
        return StartWorkAsync(async token =>
        {
            Reload();
            await CheckUpdatesAsync(token).ConfigureAwait(false);
            int count;
            lock (_sync)
            {
                count = _installed.Count;
            }

            return $"Read {count} theme{(count == 1 ? "" : "s")}.";
        });
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetHiddenAsync(string name, bool hidden, CancellationToken cancellationToken)
    {
        return Task.FromResult(ChangeConfig(config =>
        {
            config.HiddenThemes.Remove(name);
            if (hidden)
            {
                config.HiddenThemes.Add(name);
            }
        }, false));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _operation.Clear();
        }

        Publish(false);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>Turns installing themes into Steam on or off.</summary>
    /// <param name="enabled">Whether themes are installed.</param>
    /// <param name="cancellationToken">Unused; the change is immediate.</param>
    /// <returns>Whether the setting was saved.</returns>
    public Task<SteamUiCommandResult> SetThemesEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        return Task.FromResult(ChangeConfig(config => config.Enabled = enabled, true));
    }

    /// <summary>Chooses the class-translation branch and fetches its translations when it changed.</summary>
    /// <param name="branch">One of the <see cref="ThemeTranslationBranch" /> values.</param>
    /// <param name="cancellationToken">Unused; the change is immediate.</param>
    /// <returns>Whether the setting was saved, or why it was refused.</returns>
    public Task<SteamUiCommandResult> SetTranslationsBranchAsync(string branch,
        CancellationToken cancellationToken)
    {
        if (branch is not (ThemeTranslationBranch.Auto or ThemeTranslationBranch.Stable
            or ThemeTranslationBranch.Beta))
        {
            return Task.FromResult(new SteamUiCommandResult(false, "That setting is not one of the themes'."));
        }

        var branchChanged = branch != ReadConfig().TranslationsBranch;
        var result = ChangeConfig(config => config.TranslationsBranch = branch, true);
        if (result.Succeeded && branchChanged)
        {
            _operation.Track(Task.Run(() => _translations.FetchOnceAsync(_shutdown.Token)));
        }

        return Task.FromResult(result);
    }

    /// <summary>Writes one change to the themes' configuration and shows it at once.</summary>
    /// <param name="change">The change, applied to the saved section and to this service's copy.</param>
    /// <param name="stylesChanged">Whether the cascade the toolkit installs is affected.</param>
    /// <returns>Applied, or why the change could not be saved.</returns>
    private SteamUiCommandResult ChangeConfig(Action<ThemesConfig> change, bool stylesChanged)
    {
        lock (_configWrite)
        {
            try
            {
                _writeConfig(change);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or TimeoutException)
            {
                return Refuse(ex.Message);
            }

            // Shown at once rather than after the config reload reaches this service. Swapped under the
            // state lock, which the reload and the readers take too.
            lock (_sync)
            {
                var next = _config.Clone();
                change(next);
                _config = next;
            }
        }

        Publish(stylesChanged);
        return SteamUiCommandResult.Applied;
    }

    private ThemesConfig ReadConfig()
    {
        lock (_sync)
        {
            return _config;
        }
    }

    /// <summary>Reads the folder, the saved translations and Steam's link, then looks for updates.</summary>
    internal void Start()
    {
        lock (_loaderWork)
        {
            try
            {
                Directory.CreateDirectory(_loader.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Themes: the themes folder could not be created: {ex.Message}");
            }

            if (_translations.LoadFile() is { } saved)
            {
                _loader.SetMappings(saved);
            }

            _loader.Load();
            Capture();
        }

        if (_steamDirectory() is { } steam)
        {
            var beta = ThemePaths.IsSteamBetaActive(steam);
            lock (_sync)
            {
                _steamBeta = beta;
                _steamLink = "Linking Steam's themes folder…";
            }

            // Creating the junction can wait on a child process; the session start does not.
            _operation.Track(Task.Run(() =>
            {
                var link = ThemePaths.EnsureSteamLink(steam, _loader.Root);
                lock (_sync)
                {
                    _steamLink = link;
                }

                Log.Info($"Themes: {link}");
                Publish(false);
            }));
        }
        else
        {
            lock (_sync)
            {
                _steamLink = "Steam is not installed; images in themes cannot be served.";
            }
        }

        IReadOnlyList<ThemeSnapshot> installed;
        int refused;
        lock (_sync)
        {
            installed = _installed;
            refused = _loadErrors.Count;
        }

        Log.Info(
            $"Themes: {installed.Count} themes read from {_loader.Root}, "
            + $"{installed.Count(theme => theme.Enabled)} enabled, {refused} refused.");
        Publish(true);
        _operation.Track(Task.Run(() => _translations.RunUntilFetchedAsync(_shutdown.Token)));
        _operation.Track(Task.Run(() => CheckUpdatesAsync(_shutdown.Token)));
    }

    /// <summary>Takes the reloaded configuration.</summary>
    internal void ConfigurationChanged()
    {
        var reloaded = _readConfig();
        bool changed;
        bool enabledChanged;
        lock (_sync)
        {
            var previous = _config;
            _config = reloaded;
            enabledChanged = previous.Enabled != reloaded.Enabled;
            changed = enabledChanged || previous.TranslationsBranch != reloaded.TranslationsBranch
                                     || !previous.HiddenThemes.SequenceEqual(reloaded.HiddenThemes,
                                         StringComparer.Ordinal);
        }

        if (changed)
        {
            Publish(enabledChanged);
        }
    }

    /// <summary>The cascade the toolkit installs: every enabled block, in order.</summary>
    /// <returns>The blocks, or an empty cascade while themes are off.</returns>
    internal SteamThemeState ReadStyles()
    {
        lock (_sync)
        {
            if (_styles is not null && !_stylesDirty)
            {
                return _styles;
            }
        }

        // The stylesheets are read from disk, so the cascade is built under the loader's lock and
        // only kept under _sync. A change published meanwhile marks it dirty again for the next read.
        lock (_loaderWork)
        {
            bool enabled;
            long revision;
            lock (_sync)
            {
                if (_styles is not null && !_stylesDirty)
                {
                    return _styles;
                }

                enabled = _config.Enabled;
                revision = _stylesRevision;
                _stylesDirty = false;
            }

            var styles = enabled ? _loader.ActiveStyles() : [];
            lock (_sync)
            {
                _styles = new SteamThemeState(styles, revision);
                return _styles;
            }
        }
    }

    /// <summary>What the page, the section and the overlay draw, apart from a surface's browsing.</summary>
    /// <returns>
    ///     A snapshot of the current configuration, installed themes and progress, with an empty listing and no
    ///     detail; a <see cref="ThemeBrowseSession" /> adds its own.
    /// </returns>
    internal SteamThemesState ReadState()
    {
        var updates = _updates.Updates;
        lock (_sync)
        {
            var hidden = new HashSet<string>(_config.HiddenThemes, StringComparer.Ordinal);
            List<SteamThemesInstalled> themes = [];
            List<SteamThemesInstalled> presets = [];
            foreach (var theme in _installed)
            {
                var projected = Project(theme, hidden.Contains(theme.Name), updates);
                if (theme.IsPreset)
                {
                    presets.Add(projected);
                }
                else
                {
                    themes.Add(projected);
                }
            }

            var selected = presets.Where(preset => preset.Enabled).Select(preset => preset.Name).ToList();
            var settings = new SteamThemesSettings(
                _config.Enabled,
                _config.TranslationsBranch,
                _steamBeta,
                _translations.Count,
                _translations.Fetched?.ToString("u", CultureInfo.InvariantCulture),
                _loader.Root,
                _steamLink);
            return new SteamThemesState(
                string.Empty,
                themes,
                presets,
                selected.Count == 1 ? selected[0] : selected.Count > 1 ? MultiplePresetsSelected : string.Empty,
                EmptyBrowse,
                null,
                settings,
                _loadErrors,
                _operation.Busy,
                _operation.Notice,
                _operation.Error,
                updates.Values.Count(update => update.Status == ThemeStates.Outdated),
                _revision);
        }
    }

    private SteamUiCommandResult Refuse(string error)
    {
        lock (_sync)
        {
            _operation.Error = error;
        }

        Publish(false);
        return new SteamUiCommandResult(false, error);
    }

    /// <summary>Runs one immediate loader change unless another theme operation is running, then publishes it.</summary>
    /// <param name="change">The change, run with <c>_loaderWork</c> held; answers the loader's error or null.</param>
    /// <param name="notice">The notice shown once the change succeeded, or null.</param>
    /// <returns>Applied, or why the change was refused.</returns>
    private Task<SteamUiCommandResult> LoaderCommand(Func<string?> change, string? notice = null)
    {
        string? error;
        lock (_loaderWork)
        {
            if (IsBusy())
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            error = change();
            Capture();
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        if (notice is not null)
        {
            SetNotice(notice);
        }

        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private void SetNotice(string notice)
    {
        lock (_sync)
        {
            _operation.SetNotice(notice);
        }
    }

    private bool IsBusy()
    {
        lock (_sync)
        {
            return _operation.Busy;
        }
    }

    /// <summary>Publishes what the loader holds now for the readers. Called with <c>_loaderWork</c> held.</summary>
    private void Capture()
    {
        List<ThemeSnapshot> installed = [.. _loader.Themes.Select(theme => theme.Snapshot())];
        var errors = _loader.LastLoadErrors;
        lock (_sync)
        {
            _installed = installed;
            _loadErrors = errors;
            _stylesDirty = true;
        }
    }

    /// <summary>Runs store work in the background, answering the command at once.</summary>
    /// <param name="work">The work, answering the notice.</param>
    /// <param name="reload">
    ///     Whether the folder is read again afterwards, failed or not: an install or update that
    ///     stops halfway has already unpacked what came before, and the loader must see it.
    /// </param>
    private Task<SteamUiCommandResult> StartWorkAsync(Func<CancellationToken, Task<string>> work, bool reload = false)
    {
        return Task.FromResult(_operation.Start(
            "Another theme operation is still running.",
            async () =>
            {
                try
                {
                    return await work(_shutdown.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (reload && !_shutdown.IsCancellationRequested)
                    {
                        Reload();
                    }
                }
            },
            static ex => ex.Message,
            () => Publish(false),
            (_, error) =>
            {
                lock (_sync)
                {
                    _stylesDirty = true;
                }

                if (error is not null)
                {
                    Log.Warn($"Themes: {error}");
                }

                Publish(true);
            }));
    }

    private async Task UpdateOneAsync(string name, string id, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> local;
        lock (_sync)
        {
            // The theme itself is not local for this purpose: the point is to fetch it again.
            local = [.. _installed.Where(theme => theme.Name != name).Select(theme => theme.Name)];
        }

        await _installer.InstallAsync(id, local, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the folder again and republishes the cascade; the saved state decides what is on.</summary>
    private void Reload()
    {
        lock (_loaderWork)
        {
            _loader.Load();
            Capture();
        }

        _operation.Track(Task.Run(() => CheckUpdatesAsync(_shutdown.Token)));
    }

    /// <summary>Checks the store for newer versions of the themes installed now.</summary>
    private Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        List<(string Name, string Id, string Version)> installed;
        lock (_sync)
        {
            installed = [.. _installed.Select(theme => (theme.Name, theme.Id, theme.Version))];
        }

        return _updates.CheckAsync(installed, cancellationToken);
    }

    private bool WantsBetaTranslations()
    {
        lock (_sync)
        {
            return _config.TranslationsBranch switch
            {
                ThemeTranslationBranch.Beta => true,
                ThemeTranslationBranch.Stable => false,
                _ => _steamBeta
            };
        }
    }

    private void ApplyTranslations(ThemeClassMappings mappings)
    {
        lock (_loaderWork)
        {
            _loader.SetMappings(mappings);
        }

        Publish(true);
    }

    private static SteamThemesInstalled Project(ThemeSnapshot snapshot, bool hidden,
        IReadOnlyDictionary<string, (string Status, string? Latest, string? Id)> updates)
    {
        var (status, latest, _) = updates.TryGetValue(snapshot.Name, out var update)
            ? update
            : (ThemeStates.Unknown, null, null);
        return new SteamThemesInstalled(
            snapshot.Id,
            snapshot.Name,
            snapshot.DisplayName,
            snapshot.Version,
            snapshot.Author,
            snapshot.Enabled,
            hidden,
            status,
            latest,
            snapshot.Patches,
            snapshot.Dependencies);
    }

    /// <summary>Creates one surface's store browsing over this service.</summary>
    /// <param name="tab">The tab the surface opens on.</param>
    /// <returns>A session the surface owns and disposes; its reads end with this service.</returns>
    internal ThemeBrowseSession CreateBrowserSession(string tab)
    {
        return new ThemeBrowseSession(this, _client, tab, _shutdown.Token);
    }

    /// <summary>Joins a browse session's store read to the work the session shutdown waits for.</summary>
    /// <param name="work">The read.</param>
    internal void Track(Task work)
    {
        _operation.Track(work);
    }

    /// <summary>Projects one store listing with its installed state now.</summary>
    /// <param name="summary">The listing.</param>
    /// <returns>The listing as the pages draw it.</returns>
    internal SteamThemesStoreItem BrowserItem(ThemeStoreSummary summary)
    {
        lock (_sync)
        {
            return ProjectListing(summary);
        }
    }

    private SteamThemesStoreItem ProjectListing(ThemeStoreSummary summary)
    {
        var local = _installed.FirstOrDefault(theme => theme.Id == summary.Id || theme.Name == summary.Name);
        var status = local is null
            ? ThemeStates.None
            : local.Version == summary.Version
                ? ThemeStates.Installed
                : ThemeStates.Outdated;
        return new SteamThemesStoreItem(
            summary.Id,
            summary.Name,
            summary.DisplayName,
            summary.Version,
            summary.Target,
            summary.Targets,
            summary.SpecifiedAuthor.Length > 0 ? summary.SpecifiedAuthor : summary.AuthorName,
            summary.ImageIds.Count > 0 ? _client.BlobUrl(summary.ImageIds[0]) : null,
            summary.DownloadCount,
            summary.StarCount,
            summary.Updated?.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            status);
    }

    private void Publish(bool stylesChanged)
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _revision);
        if (stylesChanged)
        {
            lock (_sync)
            {
                _stylesDirty = true;
            }

            Interlocked.Increment(ref _stylesRevision);
        }

        Changed?.Invoke();
    }
}
