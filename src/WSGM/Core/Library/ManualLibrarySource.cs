using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Offers one authored command through the existing preview and apply pipeline.</summary>
/// <param name="sources">The configured authored commands belonging to this one logical source.</param>
/// <param name="profiles">The parser systems available to authored ROM entries.</param>
public sealed class ManualLibrarySource(
    IReadOnlyList<ManualShortcutConfig> sources,
    IReadOnlyList<RomSystemProfile>? profiles = null) : ILibrarySource
{
    /// <inheritdoc />
    public string Id => "manual";

    /// <inheritdoc />
    public LibrarySourceKind Kind => LibrarySourceKind.Manual;

    /// <inheritdoc />
    public string DisplayName => "Manual shortcuts";

    /// <inheritdoc />
    public SourceAvailability Detect(IReadOnlyList<UninstallEntry> programs)
    {
        return new SourceAvailability(true, "Manual shortcut");
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(IReadOnlyList<UninstallEntry> programs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DiscoveredGame> games = [];
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.RomPath is not null)
            {
                try
                {
                    if (RomLibrarySource.ReadSingle(source, profiles) is { } game)
                    {
                        games.Add(game);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log.Warn($"Manual ROM {source.Name} could not be read: {ex.Message}");
                }

                continue;
            }

            ManagedContentRecord? content = null;
            if (source.LibraryBacked)
            {
                content = ManagedContentStorage.CreateRecord(Id, source.Id, source.Name, LibrarySourceKind.Manual,
                    source.Location.Length > 0 ? source.Location : source.Name,
                    source.ContentPath.Length > 0 ? source.ContentPath : source.Target);
                content.Program = ManagedContentStorage.CapturePath(source.Target);
                content.WorkingDirectory = ManagedContentStorage.CapturePath(source.WorkingDirectory.Length > 0
                    ? source.WorkingDirectory
                    : Path.GetDirectoryName(source.Target)!, true);
                content.RawArguments = source.Arguments;
            }

            ShortcutRoute route = new("direct", "Direct", source.Target, source.WorkingDirectory, source.Arguments,
                "Runs the command you defined.");
            games.Add(DiscoveredGame.Command(Id, source.Id, source.Name, source.ContentPath,
                [route], content, source.LibraryBacked));
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(games);
    }
}
