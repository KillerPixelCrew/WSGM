using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Offers a connected card's inventory through the existing review/apply pipeline, without adopting or executing it.</summary>
/// <remarks>
///     The card is identified by its manifest UUID, read through the volume GUID it was found on. Its drive letter
///     plays no part: every path is bound to that volume.
/// </remarks>
public sealed class ManifestLibrarySource : ILibrarySource
{
    private readonly StorageLibraryRecord _library;
    private readonly IReadOnlyDictionary<string, ManagedContentRecord>? _localRecipes;

    /// <summary>Captures the observed card and optional host-only command recipes this device already has.</summary>
    public ManifestLibrarySource(StorageLibraryRecord library,
        IReadOnlyDictionary<string, ManagedContentRecord>? localRecipes = null)
    {
        _library = library.Copy();
        _localRecipes = localRecipes;
    }

    /// <inheritdoc />
    public string Id => "manifest:" + _library.Id.ToLowerInvariant();

    /// <inheritdoc />
    public string DisplayName => _library.Name;

    /// <inheritdoc />
    public LibrarySourceKind Kind => LibrarySourceKind.Folder;

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        var state = StorageLibraryStorage.InspectPath(_library.VolumeId, @".wsgm\library.json");
        return new SourceAvailability(state == ContentPathState.Present, state switch
        {
            ContentPathState.Present => DisplayName,
            ContentPathState.Missing => "This card no longer has a game list.",
            ContentPathState.VolumeAbsent => "Not connected",
            _ => "The card could not be read."
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(IReadOnlyList<UninstallEntry> programs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = StorageLibraryStorage.Read(_library.VolumeId, _library.Id);
        var context = new ManagedContentCheckContext();
        List<DiscoveredGame> games = [];
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManagedContentRecord? recipe = null;
            if (_localRecipes is not null && _localRecipes.TryGetValue(entry.Id, out var known)
                && known.Id == entry.Id
                && string.Equals(known.LibraryId, _library.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(known.BackingPath.VolumeId, _library.VolumeId, StringComparison.OrdinalIgnoreCase))
            {
                recipe = known;
            }

            var content = StorageLibraryStorage.ToManaged(entry, _library, Id, recipe);
            var present = StorageLibraryStorage.InspectPath(_library.VolumeId, entry.BackingPath.RelativePath,
                entry.BackingPath.Directory, context) == ContentPathState.Present;
            var canConfigure = !entry.RequiresLocalRecipe || recipe is not null;
            var route = new ShortcutRoute("managed", "Managed content", "", "", "",
                "WSGM validates this title and its local dependencies at launch.", ManagedId: entry.Id);
            var game = DiscoveredGame.Command(Id, entry.Id, entry.Name, content.BackingPath.AbsolutePath, [route],
                content, wrapManagedLaunch: true);
            games.Add(game with
            {
                Launch = new GameLaunch("Managed content", canConfigure,
                    canConfigure ? route.Evidence : "Configure this title's local command before importing it."),
                Notes = present
                    ? entry.SourceName.Length > 0 ? [entry.SourceName] : []
                    : ["Game files missing from this card."]
            });
        }
        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games);
    }
}
