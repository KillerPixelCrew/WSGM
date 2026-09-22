using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
///         it by choosing the same id.
///     </para>
/// </remarks>
internal sealed class SteamExtensionsTabBackend : ISteamExtensionsTabBackend
{
    /// <summary>The row WSGM's library tools live on.</summary>
    internal const string LibraryId = "wsgm.library";

    /// <summary>The action that opens the importer.</summary>
    internal const string ImportId = "wsgm.library.import";

    private const string ReservedPrefix = "wsgm.";

    private readonly CommonPluginSteamUiSource? _pluginSteamUi;
    private readonly Func<string>? _openImport;
    private readonly Func<string>? _importSourceName;

    /// <summary>Creates the tab over WSGM's own tools and an optional plugin source.</summary>
    /// <param name="pluginSteamUi">The admitted-package projection, or null when there is none.</param>
    /// <param name="openImport">Returns the route that opens the importer, or null without one.</param>
    /// <param name="importSourceName">What the importer imports from, for the row's detail line.</param>
    internal SteamExtensionsTabBackend(
        CommonPluginSteamUiSource? pluginSteamUi,
        Func<string>? openImport,
        Func<string>? importSourceName)
    {
        _pluginSteamUi = pluginSteamUi;
        _openImport = openImport;
        _importSourceName = importSourceName;
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
            return new SteamUiCommandResult(true, null, JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["route"] = _openImport() }));
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
        // WSGM's own rows declare no settings, so a configure for one is a stale click.
        if (id.StartsWith(ReservedPrefix, StringComparison.Ordinal) || _pluginSteamUi is null)
        {
            return new SteamUiCommandResult(false, "That setting is no longer available.");
        }

        return await _pluginSteamUi
            .ConfigureAsync(id, key, value, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the tab should currently show.</summary>
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
                $"Add your installed {_importSourceName?.Invoke() ?? "Xbox"} games to Steam.",
                [new SteamExtensionsTabAction(ImportId, "Import games…")]));
        }

        if (_pluginSteamUi is not null)
        {
            var plugins = _pluginSteamUi.ReadExtensionsTab();
            items.AddRange(plugins.Items
                .Where(item => !item.Id.StartsWith(ReservedPrefix, StringComparison.Ordinal)));
        }

        return new SteamExtensionsTabState(items);
    }
}
