using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The Steam themes: CSSLoader-compatible themes browsed from DeckThemes, installed into the
///     themes folder and published into Big Picture through the toolkit's theme-styles surface.
/// </summary>
/// <remarks>
///     <para>
///         One owner for the loader, the store, the class translations and the update check. The
///         Themes page in Steam, the Quick Access section and the overlay all read
///         <see cref="ReadState" /> and call the same <see cref="ISteamThemesBackend" /> methods, so a
///         change made in one is what the others show next. The toolkit's gate reads
///         <see cref="ReadStyles" />, which is the cascade the loader answers under its own revision.
///     </para>
///     <para>
///         A command that touches only the folder answers when it is done; one that asks the store
///         answers at once and finishes in the background, raising <see cref="Changed" /> as it goes,
///         the way the Game Library's page does. Nothing is retried on its own: a failed install says
///         so and waits for the user.
///     </para>
/// </remarks>
internal sealed class ThemeService : ISteamThemesBackend, IDisposable, IChangeSource, IExtensionsTabSection
{
    /// <summary>The section's item id.</summary>
    internal const string ExtensionsId = "wsgm.themes";

    /// <summary>The action that opens the Browse tab.</summary>
    internal const string ExtensionsBrowseId = "wsgm.themes.browse";

    /// <summary>The action that opens the Installed tab.</summary>
    internal const string ExtensionsManageId = "wsgm.themes.manage";

    /// <summary>The action that installs every update.</summary>
    internal const string ExtensionsUpdateAllId = "wsgm.themes.update-all";

    /// <summary>The action that reads the folder again.</summary>
    internal const string ExtensionsRefreshId = "wsgm.themes.refresh";

    /// <summary>How long a failed translation fetch waits before the next try, as CSS Loader waits.</summary>
    private static readonly TimeSpan TranslationsRetry = TimeSpan.FromSeconds(60);

    private readonly ThemeStoreClient _client;
    private readonly ThemeInstaller _installer;
    private readonly ThemeLoader _loader;
    private readonly Func<ThemesConfig> _readConfig;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string?> _steamDirectory;
    private readonly Lock _sync = new();
    private readonly Action<Action<ThemesConfig>> _writeConfig;
    private string _activeTab = "browse";
    private string? _browseError;
    private ThemeStoreFilters? _browseFilters;
    private List<SteamThemesStoreItem> _browseItems = [];
    private bool _browseLoading;
    private int _browsePage;
    private ThemeStoreQuery _browseQuery = ThemeStoreQuery.Default;
    private int _browseSequence;
    private int _browseTotal;
    private bool _busy;
    private ThemesConfig _config;
    private SteamThemesDetail? _detail;
    private bool _disposed;
    private string? _error;
    private string? _notice;
    private long _revision;
    private bool _steamBeta;
    private string _steamLink = "Not checked yet.";
    private SteamThemeState? _styles;
    private bool _stylesDirty = true;
    private long _stylesRevision;
    private int _translationsCount;
    private DateTimeOffset? _translationsFetched;
    private Dictionary<string, (string Status, string? Latest, string? Id)> _updates = new(StringComparer.Ordinal);

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
    }

    /// <summary>The revision of the state <see cref="ReadState" /> answers.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>The revision of the cascade <see cref="ReadStyles" /> answers.</summary>
    internal long StylesRevision => Interlocked.Read(ref _stylesRevision);

    /// <summary>Whether enabled themes are installed into Steam.</summary>
    internal bool Enabled => _config.Enabled;

    /// <summary>The themes folder.</summary>
    internal string Root => _loader.Root;

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
    public string SectionId => ExtensionsId;

    /// <summary>The Quick Access section: the profile, every theme not hidden, and its patches under it.</summary>
    /// <returns>The item.</returns>
    public SteamExtensionsTabItem ReadExtensionsItem()
    {
        var state = ReadState();
        List<SteamExtensionsTabAction> actions =
        [
            new(ExtensionsBrowseId, "Browse themes…"),
            new(ExtensionsManageId, "Manage…")
        ];
        if (state.Updates > 0)
        {
            actions.Add(new SteamExtensionsTabAction(ExtensionsUpdateAllId, $"Update all ({state.Updates})"));
        }

        actions.Add(new SteamExtensionsTabAction(ExtensionsRefreshId, "Refresh"));

        List<SteamExtensionsTabSetting> settings = [];
        if (state.Presets.Count > 0)
        {
            settings.Add(new SteamExtensionsTabSetting(
                "profile", "Profile", "text",
                TextValue: state.Presets.Any(preset => preset.Name == state.SelectedPreset) ? state.SelectedPreset : "",
                Choices: ["", .. state.Presets.Select(preset => preset.Name)],
                ChoiceLabels: ["None", .. state.Presets.Select(preset => preset.DisplayName)]));
        }

        foreach (var theme in state.Themes.Where(theme => !theme.Hidden))
        {
            var key = ExtensionsKey("theme", theme.Name);
            var description = theme.Status == "outdated"
                ? $"Update available · {theme.Author}"
                : string.IsNullOrEmpty(theme.Author)
                    ? theme.Version
                    : $"{theme.Version} · {theme.Author}";
            settings.Add(new SteamExtensionsTabSetting(key, theme.DisplayName, "boolean", theme.Enabled,
                Description: description, Highlight: theme.Status == "outdated"));
            foreach (var patch in theme.Patches)
            {
                var patchKey = ExtensionsKey("patch", theme.Name, patch.Name);
                switch (patch.Type)
                {
                    case "checkbox":
                        settings.Add(new SteamExtensionsTabSetting(
                            patchKey, patch.Name, "boolean", patch.Value == "Yes", Parent: key));
                        break;
                    case "slider":
                        settings.Add(new SteamExtensionsTabSetting(
                            patchKey, patch.Name, "number",
                            NumberValue: Math.Max(0, patch.Options.ToList().IndexOf(patch.Value)),
                            Choices: patch.Options, Parent: key));
                        break;
                    case "none":
                        break;
                    default:
                        settings.Add(new SteamExtensionsTabSetting(
                            patchKey, patch.Name, "text", TextValue: patch.Value, Choices: patch.Options, Parent: key));
                        break;
                }

                foreach (var component in patch.Components.Where(component => component.On == patch.Value))
                {
                    settings.Add(new SteamExtensionsTabSetting(
                        ExtensionsKey("component", theme.Name, patch.Name, component.Name),
                        component.Name,
                        component.Type == "color-picker" ? "color" : "text",
                        TextValue: component.Value,
                        Parent: key));
                }
            }
        }

        var hiddenCount = state.Themes.Count(theme => theme.Hidden);
        var detail = !state.Settings.Enabled
            ? "Off in Settings"
            : state.Themes.Count == 0
                ? "No themes installed"
                : $"{state.Themes.Count(theme => theme.Enabled)} of {state.Themes.Count} enabled"
                  + (state.Updates > 0 ? $" · {state.Updates} update{(state.Updates == 1 ? "" : "s")}" : string.Empty)
                  + (hiddenCount > 0 ? $" · {hiddenCount} hidden" : string.Empty);
        return new SteamExtensionsTabItem(
            ExtensionsId,
            "Themes",
            string.Empty,
            state.Busy ? "Working…" : "Ready",
            detail,
            actions,
            settings,
            state.Revision);
    }

    /// <summary>Answers one of the section's actions.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result, carrying the page's route for the two that open it.</returns>
    public async Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        switch (id)
        {
            case ExtensionsBrowseId:
                await SetTabAsync("browse", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamThemesSurface.Route);
            case ExtensionsManageId:
                await SetTabAsync("installed", cancellationToken).ConfigureAwait(false);
                return SteamUiCommandResult.Route(SteamThemesSurface.Route);
            case ExtensionsUpdateAllId:
                return await UpdateAllAsync(cancellationToken).ConfigureAwait(false);
            case ExtensionsRefreshId:
                return await RefreshAsync(cancellationToken).ConfigureAwait(false);
            default:
                return new SteamUiCommandResult(false, "That entry is no longer available.");
        }
    }

    /// <summary>Answers one of the section's settings.</summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The value.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>The result.</returns>
    public Task<SteamUiCommandResult> ConfigureExtensionAsync(
        string key, JsonElement value, CancellationToken cancellationToken)
    {
        if (key == "profile")
        {
            return value.ValueKind == JsonValueKind.String
                ? SetProfileAsync(value.GetString() ?? string.Empty, cancellationToken)
                : Task.FromResult(new SteamUiCommandResult(false, "The profile value is invalid."));
        }

        // A key names its theme, patch or component by a digest of the names, found again by
        // building the same digest over what is installed now: names may hold any character, and a
        // key stays inside the tab's bound however long they are.
        var state = ReadState();
        foreach (var theme in state.Themes)
        {
            if (key == ExtensionsKey("theme", theme.Name))
            {
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? SetEnabledAsync(theme.Name, value.GetBoolean(), cancellationToken)
                    : Task.FromResult(new SteamUiCommandResult(false, "The theme value is invalid."));
            }

            foreach (var patch in theme.Patches)
            {
                if (key == ExtensionsKey("patch", theme.Name, patch.Name))
                {
                    var option = value.ValueKind switch
                    {
                        JsonValueKind.True => "Yes",
                        JsonValueKind.False => "No",
                        JsonValueKind.Number when value.TryGetInt32(out var index) && index >= 0
                            && index < patch.Options.Count =>
                            patch.Options[index],
                        JsonValueKind.String => value.GetString(),
                        _ => null
                    };
                    return option is null
                        ? Task.FromResult(new SteamUiCommandResult(false, "The patch value is invalid."))
                        : SetPatchAsync(theme.Name, patch.Name, option, cancellationToken);
                }

                foreach (var component in patch.Components)
                {
                    if (key == ExtensionsKey("component", theme.Name, patch.Name, component.Name))
                    {
                        return value.ValueKind == JsonValueKind.String
                            ? SetComponentAsync(theme.Name, patch.Name, component.Name,
                                value.GetString() ?? string.Empty, cancellationToken)
                            : Task.FromResult(new SteamUiCommandResult(false, "The component value is invalid."));
                    }
                }
            }
        }

        return Task.FromResult(new SteamUiCommandResult(false, "That setting is no longer available."));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetTabAsync(string tab, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _activeTab = tab;
            _detail = null;
        }

        Publish(false);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> BrowseAsync(
        string filter, string order, string search, CancellationToken cancellationToken)
    {
        ThemeStoreQuery query;
        int sequence;
        lock (_sync)
        {
            query = new ThemeStoreQuery(
                1,
                ThemeStoreQuery.Default.PerPage,
                filter.Length == 0 ? ThemeStoreQuery.AllFilter : filter,
                order.Length == 0 ? ThemeStoreQuery.DefaultOrder : order,
                search);
            _browseQuery = query;
            _browseItems = [];
            _browseTotal = 0;
            _browsePage = 0;
            _browseLoading = true;
            _browseError = null;
            sequence = ++_browseSequence;
        }

        Publish(false);
        _ = Task.Run(() => FetchPageAsync(query, sequence, false, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken)
    {
        ThemeStoreQuery query;
        int sequence;
        lock (_sync)
        {
            if (_browseLoading)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "The store is still answering."));
            }

            if (_browseItems.Count >= _browseTotal)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Every matching theme is listed."));
            }

            query = _browseQuery with { Page = _browsePage + 1 };
            _browseQuery = query;
            _browseLoading = true;
            _browseError = null;
            sequence = ++_browseSequence;
        }

        Publish(false);
        _ = Task.Run(() => FetchPageAsync(query, sequence, true, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> OpenAsync(string id, CancellationToken cancellationToken)
    {
        SteamThemesStoreItem? listing;
        lock (_sync)
        {
            listing = _browseItems.FirstOrDefault(item => item.Id == id);
            _detail = new SteamThemesDetail(
                listing ?? new SteamThemesStoreItem(id, string.Empty, "Loading…", string.Empty, string.Empty, [],
                    string.Empty, null, 0, 0, null, "none"),
                string.Empty,
                [],
                [],
                true,
                null);
        }

        Publish(false);
        _ = Task.Run(() => FetchDetailAsync(id, _shutdown.Token));
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> CloseDetailAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _detail = null;
        }

        Publish(false);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> InstallAsync(string id, CancellationToken cancellationToken)
    {
        return StartWorkAsync(async token =>
        {
            IReadOnlyCollection<string> local;
            lock (_sync)
            {
                local = [.. _loader.Themes.Select(theme => theme.Name)];
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
        string? id;
        lock (_sync)
        {
            id = _updates.TryGetValue(name, out var update) && update.Status == "outdated" ? update.Id : null;
        }

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
        List<(string Name, string Id)> outdated;
        lock (_sync)
        {
            outdated =
            [
                .. _updates.Where(pair => pair.Value.Status == "outdated" && pair.Value.Id is not null)
                    .Select(pair => (pair.Key, pair.Value.Id!))
            ];
        }

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
        string? error;
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            error = _loader.DeleteTheme(name);
            _updates.Remove(name);
            _stylesDirty = true;
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        SetNotice($"Deleted {name}.");
        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        string? error;
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            error = _loader.SetThemeState(name, enabled);
            _stylesDirty = true;
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetPatchAsync(
        string theme, string patch, string value, CancellationToken cancellationToken)
    {
        string? error;
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            error = _loader.SetPatch(theme, patch, value);
            _stylesDirty = true;
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetComponentAsync(
        string theme, string patch, string component, string value, CancellationToken cancellationToken)
    {
        string? error;
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            error = _loader.SetComponent(theme, patch, component, value);
            _stylesDirty = true;
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetProfileAsync(string name, CancellationToken cancellationToken)
    {
        string? error = null;
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            // The profile on now goes off first, with the themes only it turned on; then the chosen
            // one comes on with its own, as CSS Loader's profile dropdown does it.
            foreach (var preset in _loader.Themes.Where(theme => theme.IsPreset && theme.Enabled && theme.Name != name))
            {
                error ??= _loader.SetThemeState(preset.Name, false);
            }

            if (name.Length > 0)
            {
                error ??= _loader.SetThemeState(name, true);
            }

            _stylesDirty = true;
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

        Publish(true);
        return Task.FromResult(SteamUiCommandResult.Applied);
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
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            combined = _loader.Themes.Count(theme => theme.Enabled && !theme.IsPreset);
            error = _loader.GeneratePreset(name.Trim());
            if (error is null)
            {
                Reload();
            }
        }

        if (error is not null)
        {
            return Task.FromResult(Refuse(error));
        }

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
                count = _loader.Themes.Count;
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
    public Task<SteamUiCommandResult> SetSettingAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        switch (key)
        {
            case "enabled" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                var enabled = value.GetBoolean();
                return Task.FromResult(ChangeConfig(config => config.Enabled = enabled, true));
            case "translationsBranch" when value.ValueKind == JsonValueKind.String
                                           && value.GetString() is { } branch
                                           && branch is ThemeTranslationBranch.Auto or ThemeTranslationBranch.Stable
                                               or ThemeTranslationBranch.Beta:
                var branchChanged = branch != _config.TranslationsBranch;
                var result = ChangeConfig(config => config.TranslationsBranch = branch, true);
                if (result.Succeeded && branchChanged)
                {
                    _ = Task.Run(() => FetchTranslationsOnceAsync(_shutdown.Token));
                }

                return Task.FromResult(result);
            default:
                return Task.FromResult(new SteamUiCommandResult(false, "That setting is not one of the themes'."));
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> DismissAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _notice = null;
            _error = null;
        }

        Publish(false);
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    /// <summary>The Quick Access key of a theme, a patch or a component.</summary>
    /// <param name="kind"><c>theme</c>, <c>patch</c> or <c>component</c>.</param>
    /// <param name="names">The theme's name, then the patch's and the component's as the kind needs.</param>
    /// <returns>The kind and a digest of the names: no name's characters reach the key.</returns>
    internal static string ExtensionsKey(string kind, params string[] names)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', names)));
        return kind + ":" + Convert.ToHexString(digest, 0, 8);
    }

    /// <summary>Writes one change to the themes' configuration and shows it at once.</summary>
    /// <param name="change">The change, applied to the saved section and to this service's copy.</param>
    /// <param name="stylesChanged">Whether the cascade the toolkit installs is affected.</param>
    /// <returns>Applied, or why the change could not be saved.</returns>
    private SteamUiCommandResult ChangeConfig(Action<ThemesConfig> change, bool stylesChanged)
    {
        try
        {
            _writeConfig(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TimeoutException)
        {
            return Refuse(ex.Message);
        }

        // Shown at once rather than after the config reload reaches this service.
        var next = _config.Clone();
        change(next);
        _config = next;
        Publish(stylesChanged);
        return SteamUiCommandResult.Applied;
    }

    /// <summary>Reads the folder, the saved translations and Steam's link, then looks for updates.</summary>
    internal void Start()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_loader.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Themes: the themes folder could not be created: {ex.Message}");
            }

            LoadTranslationsFile();
            _loader.Load();
            if (_steamDirectory() is { } steam)
            {
                _steamBeta = ThemePaths.IsSteamBetaActive(steam);
                _steamLink = "Linking Steam's themes folder…";
                // Creating the junction can wait on a child process; the session start does not.
                _ = Task.Run(() =>
                {
                    var link = ThemePaths.EnsureSteamLink(steam, _loader.Root);
                    lock (_sync)
                    {
                        _steamLink = link;
                    }

                    Log.Info($"Themes: {link}");
                    Publish(false);
                });
            }
            else
            {
                _steamLink = "Steam is not installed; images in themes cannot be served.";
            }

            _stylesDirty = true;
        }

        Log.Info(
            $"Themes: {_loader.Themes.Count} themes read from {_loader.Root}, "
            + $"{_loader.Themes.Count(theme => theme.Enabled)} enabled, {_loader.LastLoadErrors.Count} refused.");
        Publish(true);
        _ = Task.Run(() => FetchTranslationsLoopAsync(_shutdown.Token));
        _ = Task.Run(() => CheckUpdatesAsync(_shutdown.Token));
    }

    /// <summary>Takes the reloaded configuration.</summary>
    internal void ConfigurationChanged()
    {
        var previous = _config;
        _config = _readConfig();
        if (previous.Enabled != _config.Enabled || previous.TranslationsBranch != _config.TranslationsBranch
                                                || !previous.HiddenThemes.SequenceEqual(_config.HiddenThemes,
                                                    StringComparer.Ordinal))
        {
            Publish(previous.Enabled != _config.Enabled);
        }
    }

    /// <summary>The cascade the toolkit installs: every enabled block, in order.</summary>
    /// <returns>The blocks, or an empty cascade while themes are off.</returns>
    internal SteamThemeState ReadStyles()
    {
        lock (_sync)
        {
            if (_styles is null || _stylesDirty)
            {
                var styles = _config.Enabled ? _loader.ActiveStyles() : [];
                _styles = new SteamThemeState(styles, _stylesRevision);
                _stylesDirty = false;
            }

            return _styles;
        }
    }

    /// <summary>What the page, the section and the overlay draw.</summary>
    internal SteamThemesState ReadState()
    {
        lock (_sync)
        {
            var hidden = new HashSet<string>(_config.HiddenThemes, StringComparer.Ordinal);
            List<SteamThemesInstalled> themes = [];
            List<SteamThemesInstalled> presets = [];
            foreach (var theme in _loader.Themes)
            {
                var projected = Project(theme, hidden.Contains(theme.Name));
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
            var browse = new SteamThemesBrowse(
                _browseQuery.Filter,
                _browseQuery.Order,
                _browseQuery.Search,
                _browseFilters?.Filters ?? new Dictionary<string, int>(StringComparer.Ordinal),
                _browseFilters?.Orders ?? [ThemeStoreQuery.DefaultOrder],
                _browseItems,
                _browseTotal,
                _browsePage,
                _browseLoading,
                _browseError);
            var settings = new SteamThemesSettings(
                _config.Enabled,
                _config.TranslationsBranch,
                _steamBeta,
                _translationsCount,
                _translationsFetched?.ToString("u", CultureInfo.InvariantCulture),
                _loader.Root,
                _steamLink);
            return new SteamThemesState(
                _activeTab,
                themes,
                presets,
                selected.Count == 1 ? selected[0] : selected.Count > 1 ? "Invalid State" : string.Empty,
                browse,
                _detail,
                settings,
                _loader.LastLoadErrors,
                _busy,
                _notice,
                _error,
                _updates.Values.Count(update => update.Status == "outdated"),
                _revision);
        }
    }

    private SteamUiCommandResult Refuse(string error)
    {
        lock (_sync)
        {
            _error = error;
        }

        Publish(false);
        return new SteamUiCommandResult(false, error);
    }

    private void SetNotice(string notice)
    {
        lock (_sync)
        {
            _notice = notice;
            _error = null;
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
        lock (_sync)
        {
            if (_busy)
            {
                return Task.FromResult(new SteamUiCommandResult(false, "Another theme operation is still running."));
            }

            _busy = true;
            _error = null;
            _notice = null;
        }

        Publish(false);
        _ = Task.Run(async () =>
        {
            string? notice = null;
            string? error = null;
            try
            {
                notice = await work(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                error = "Cancelled.";
            }
            catch (ThemeStoreException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                error = ex.Message;
            }

            if (reload && !_shutdown.IsCancellationRequested)
            {
                Reload();
            }

            lock (_sync)
            {
                _busy = false;
                _notice = notice;
                _error = error;
                _stylesDirty = true;
            }

            if (error is not null)
            {
                Log.Warn($"Themes: {error}");
            }

            Publish(true);
        });
        return Task.FromResult(SteamUiCommandResult.Applied);
    }

    private async Task UpdateOneAsync(string name, string id, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> local;
        lock (_sync)
        {
            // The theme itself is not local for this purpose: the point is to fetch it again.
            local = [.. _loader.Themes.Where(theme => theme.Name != name).Select(theme => theme.Name)];
        }

        await _installer.InstallAsync(id, local, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the folder again and republishes the cascade; the saved state decides what is on.</summary>
    private void Reload()
    {
        lock (_sync)
        {
            _loader.Load();
            _stylesDirty = true;
        }

        _ = Task.Run(() => CheckUpdatesAsync(_shutdown.Token));
    }

    private async Task FetchPageAsync(ThemeStoreQuery query, int sequence, bool append,
        CancellationToken cancellationToken)
    {
        ThemeStoreFilters? filters = null;
        ThemePage? page = null;
        string? error = null;
        try
        {
            if (_browseFilters is null)
            {
                try
                {
                    filters = await _client.FiltersAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ThemeStoreException ex)
                {
                    Log.Warn($"Themes: the store's filters could not be read: {ex.Message}");
                }
            }

            page = await _client.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (ThemeStoreException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (sequence != _browseSequence)
            {
                return;
            }

            _browseFilters ??= filters;
            _browseLoading = false;
            _browseError = error;
            if (page is not null)
            {
                var items = page.Items.Select(ProjectListing).ToList();
                _browseItems = append ? [.. _browseItems, .. items] : items;
                _browseTotal = page.Total;
                _browsePage = query.Page;
            }
        }

        Publish(false);
    }

    private async Task FetchDetailAsync(string id, CancellationToken cancellationToken)
    {
        ThemeStoreDetails? details = null;
        string? error = null;
        try
        {
            details = await _client.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ThemeStoreException ex)
        {
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (_detail is null || _detail.Item.Id != id)
            {
                return;
            }

            if (details is null)
            {
                _detail = _detail with { Loading = false, Error = error };
            }
            else
            {
                var installed = _loader.Themes.Select(theme => theme.Name).ToHashSet(StringComparer.Ordinal);
                _detail = new SteamThemesDetail(
                    ProjectListing(details.Summary),
                    details.Description,
                    [.. details.Summary.ImageIds.Select(_client.BlobUrl)],
                    [
                        .. details.Dependencies.Select(dependency => new SteamThemesDependency(
                            dependency.Id, dependency.Name, dependency.DisplayName,
                            installed.Contains(dependency.Name)))
                    ],
                    false,
                    null);
            }
        }

        Publish(false);
    }

    private async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        List<(string Name, string Id, string Version)> installed;
        lock (_sync)
        {
            installed = [.. _loader.Themes.Select(theme => (theme.Name, theme.Id, theme.Version))];
        }

        if (installed.Count == 0)
        {
            lock (_sync)
            {
                _updates = new Dictionary<string, (string, string?, string?)>(StringComparer.Ordinal);
            }

            Publish(false);
            return;
        }

        IReadOnlyList<ThemeStoreSummary> remote;
        try
        {
            remote = await _client.LookUpAsync(
                    [.. installed.Select(theme => theme.Id).Distinct(StringComparer.Ordinal)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ThemeStoreException ex)
        {
            Log.Warn($"Themes: the update check could not reach the store: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dictionary<string, (string Status, string? Latest, string? Id)> updates = new(StringComparer.Ordinal);
        foreach (var (name, id, version) in installed)
        {
            var entry = remote.FirstOrDefault(candidate => candidate.Id == id || candidate.Name == id);
            updates[name] = entry is null
                ? ("local", null, null)
                : entry.Version == version
                    ? ("installed", null, entry.Id)
                    : ("outdated", entry.Version, entry.Id);
        }

        lock (_sync)
        {
            _updates = updates;
        }

        Publish(false);
    }

    private async Task FetchTranslationsLoopAsync(CancellationToken cancellationToken)
    {
        // Every minute until one fetch succeeds, as CSS Loader retries; a later branch change asks
        // again on its own.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await FetchTranslationsOnceAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await Task.Delay(TranslationsRetry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> FetchTranslationsOnceAsync(CancellationToken cancellationToken)
    {
        var beta = _config.TranslationsBranch switch
        {
            ThemeTranslationBranch.Beta => true,
            ThemeTranslationBranch.Stable => false,
            _ => _steamBeta
        };
        string text;
        try
        {
            text = await _client.TranslationsAsync(beta, cancellationToken).ConfigureAwait(false);
        }
        catch (ThemeStoreException ex)
        {
            Log.Change("themes.translations", $"Themes: class translations not fetched: {ex.Message}", LogLevel.Warn);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        ThemeClassMappings mappings;
        try
        {
            mappings = ThemeClassMappings.Parse(text);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Themes: the class translations could not be read: {ex.Message}");
            return false;
        }

        try
        {
            Directory.CreateDirectory(_loader.Root);
            AtomicFile.WriteText(Path.Combine(_loader.Root, ThemePaths.TranslationsFileName), text, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Themes: the class translations could not be kept: {ex.Message}");
        }

        lock (_sync)
        {
            _loader.SetMappings(mappings);
            _translationsCount = mappings.Count;
            _translationsFetched = DateTimeOffset.UtcNow;
            _stylesDirty = true;
        }

        Log.Change("themes.translations",
            $"Themes: {mappings.Count} class translations fetched from the {(beta ? "beta" : "stable")} table.");
        Publish(true);
        return true;
    }

    private void LoadTranslationsFile()
    {
        var path = Path.Combine(_loader.Root, ThemePaths.TranslationsFileName);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var mappings = ThemeClassMappings.Parse(File.ReadAllText(path));
            _loader.SetMappings(mappings);
            _translationsCount = mappings.Count;
            _translationsFetched = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Themes: the saved class translations could not be read: {ex.Message}");
        }
    }

    private SteamThemesInstalled Project(InstalledTheme theme, bool hidden)
    {
        var snapshot = theme.Snapshot();
        var (status, latest, _) = _updates.TryGetValue(theme.Name, out var update) ? update : ("unknown", null, null);
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

    private SteamThemesStoreItem ProjectListing(ThemeStoreSummary summary)
    {
        var local = _loader.Themes.FirstOrDefault(theme => theme.Id == summary.Id || theme.Name == summary.Name);
        var status = local is null ? "none" : local.Version == summary.Version ? "installed" : "outdated";
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
