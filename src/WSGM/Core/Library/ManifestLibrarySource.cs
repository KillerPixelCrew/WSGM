using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Offers registered card inventory through the existing review/apply pipeline, without adopting or executing it.</summary>
public sealed class ManifestLibrarySource : ILibrarySource
{
    private readonly StorageLibraryRecord _library;
    private readonly IReadOnlyDictionary<string, ManagedContentRecord>? _localRecipes;

    /// <summary>Captures a candidate binding and optional previously accepted host-only command recipes.</summary>
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
        var check = StorageLibraryStorage.InspectPath(_library.VolumeId, @".wsgm\library.json");
        return new SourceAvailability(check.State == StorageLibraryPathState.Present,
            check.State == StorageLibraryPathState.Present ? DisplayName : check.Detail);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(IReadOnlyList<UninstallEntry> programs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = StorageLibraryStorage.Read(_library.VolumeId, _library.Id);
        List<DiscoveredGame> games = [];
        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManagedContentRecord? recipe = null;
            if (_localRecipes is not null && _localRecipes.TryGetValue(entry.Id, out var known)
                && known.Id == entry.Id && known.LibraryBindingAccepted && !known.InventoryRemoved
                && string.Equals(known.LibraryId, _library.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(known.BackingPath.VolumeId, _library.VolumeId, StringComparison.OrdinalIgnoreCase))
            {
                recipe = known;
            }

            var content = StorageLibraryStorage.ToManaged(entry, _library, Id, localRecipe: recipe);
            var path = StorageLibraryStorage.InspectPath(_library.VolumeId, entry.BackingPath.RelativePath,
                entry.BackingPath.Directory);
            var canConfigure = !entry.RequiresLocalRecipe || recipe is not null;
            var route = new ShortcutRoute("managed", "Managed content", "", "", "",
                "WSGM validates this title and its local dependencies at launch.", ManagedId: entry.Id);
            var game = DiscoveredGame.Command(Id, entry.Id, entry.Name, path.Path, [route], content,
                wrapManagedLaunch: true);
            games.Add(game with
            {
                Launch = new GameLaunch("Managed content", canConfigure,
                    canConfigure ? route.Evidence : "Configure this title's local command before importing it."),
                Notes = path.State == StorageLibraryPathState.Present
                    ? entry.SourceName.Length > 0 ? [entry.SourceName] : []
                    : [path.Detail]
            });
        }
        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games);
    }
}
