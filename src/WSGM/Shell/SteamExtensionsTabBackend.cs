using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>
///     The Quick Access plugin tab: WSGM's own tools first, then whatever admitted packages
///     contribute.
/// </summary>
/// <remarks>
///     <para>
///         WSGM's rows are not conditional on a plugin source existing. The tab used to render only
///         what packages put in it, which meant an install with no packages — every install, as it
///         turned out — showed an empty tab.
///     </para>
///     <para>
///         WSGM's ids carry a reserved prefix, so a package can neither answer for one nor displace
///         it by choosing the same id. Every section folds through the Quick Access fold surface,
///         WSGM's and a package's alike, so a package is never asked to remember presentation state.
///     </para>
/// </remarks>
internal sealed class SteamExtensionsTabBackend : ISteamExtensionsTabBackend
{
    /// <summary>The row WSGM's library tools live on.</summary>
    internal const string LibraryId = "wsgm.library";

    /// <summary>The action that opens the importer.</summary>
    internal const string ImportId = "wsgm.library.import";

    /// <summary>The row for WSGM's emulator management tools.</summary>
    internal const string EmulatorsId = "wsgm.emulators";

    /// <summary>The action that opens the emulator downloader and updater directly.</summary>
    internal const string OpenEmulatorsId = "wsgm.emulators.open";

    private const string ReservedPrefix = "wsgm.";
    private readonly bool _emulatorsAvailable;
    private readonly Func<string>? _openImport;

    private readonly CommonPluginSteamUiSource? _pluginSteamUi;
    private readonly IReadOnlyList<IExtensionsTabSection> _sections;
    private readonly Func<string>? _sourceNames;

    /// <summary>Creates the tab over WSGM's own tools and an optional plugin source.</summary>
    /// <param name="pluginSteamUi">The admitted-package projection, or null when there is none.</param>
    /// <param name="openImport">Returns the route that opens the importer, or null without one.</param>
    /// <param name="sourceNames">The sources the library reads, for the row's detail line.</param>
    /// <param name="sections">WSGM's own sections after the library, in order: the themes, the boot movie.</param>
    /// <param name="emulatorsAvailable">Whether emulator management is available in this session.</param>
    internal SteamExtensionsTabBackend(
        CommonPluginSteamUiSource? pluginSteamUi,
        Func<string>? openImport,
        Func<string>? sourceNames,
        IReadOnlyList<IExtensionsTabSection>? sections = null,
        bool emulatorsAvailable = false)
    {
        _pluginSteamUi = pluginSteamUi;
        _openImport = openImport;
        _sourceNames = sourceNames;
        _sections = sections ?? [];
        _emulatorsAvailable = emulatorsAvailable;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ActivateAsync(string id, CancellationToken cancellationToken)
    {
        if (id == ImportId)
        {
            if (_openImport is null)
            {
                return new SteamUiCommandResult(false, "The library importer is unavailable in this session.");
            }

            // The route travels in the payload the gate reads, the same shape every other
            // page-opening action uses.
            return SteamUiCommandResult.Route(_openImport());
        }

        if (id == OpenEmulatorsId)
        {
            return !_emulatorsAvailable
                ? new SteamUiCommandResult(false, "The emulator downloader and updater is unavailable in this session.")
                : SteamUiCommandResult.Route(SteamEmulatorSurface.Route);
        }

        if (_sections.FirstOrDefault(section => id.StartsWith(section.SectionId + ".", StringComparison.Ordinal))
            is { } owner)
        {
            return await owner.ActivateExtensionAsync(id, cancellationToken).ConfigureAwait(false);
        }

        if (id.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            return new SteamUiCommandResult(false, "That entry is no longer available.");
        }

        return _pluginSteamUi is null
            ? new SteamUiCommandResult(false, "That entry is no longer available.")
            : await _pluginSteamUi.ActivateAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ConfigureAsync(
        string id, string key, JsonElement value, long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (_sections.FirstOrDefault(section => section.SectionId == id) is { } owner)
        {
            return await owner.ConfigureExtensionAsync(key, value, cancellationToken).ConfigureAwait(false);
        }

        // WSGM's other rows declare no settings, so a configure for one is a stale click.
        if (id.StartsWith(ReservedPrefix, StringComparison.Ordinal) || _pluginSteamUi is null)
        {
            return new SteamUiCommandResult(false, "That setting is no longer available.");
        }

        return await _pluginSteamUi
            .ConfigureAsync(id, key, value, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the tab should currently show.</summary>
    /// <returns>Current built-in and plugin contributions; plugins using the reserved WSGM prefix are omitted.</returns>
    internal SteamExtensionsTabState ReadState()
    {
        List<SteamExtensionsTabItem> items = [];
        if (_openImport is not null)
        {
            items.Add(new SteamExtensionsTabItem(
                LibraryId,
                "Game Library",
                string.Empty,
                "Ready",
                $"Bring your {_sourceNames?.Invoke() ?? "other launchers'"} games into Steam.",
                [new SteamExtensionsTabAction(ImportId, "Import games…")]));
        }

        if (_emulatorsAvailable)
        {
            items.Add(new SteamExtensionsTabItem(
                EmulatorsId,
                "Emulators",
                string.Empty,
                "Ready",
                "Install, update and configure the emulators used by your ROM libraries.",
                [new SteamExtensionsTabAction(OpenEmulatorsId, "Emulator Downloader / Updater")]));
        }

        items.AddRange(_sections.Select(section => section.ReadExtensionsItem()));

        if (_pluginSteamUi is not null)
        {
            var plugins = _pluginSteamUi.ReadExtensionsTab();
            items.AddRange(plugins.Items
                .Where(item => !item.Id.StartsWith(ReservedPrefix, StringComparison.Ordinal)));
        }

        return new SteamExtensionsTabState(items);
    }
}
