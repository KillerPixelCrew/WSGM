using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Host-owned card inventory I/O and portable identity matching.</summary>
public static class StorageLibraryStorage
{
    private const string ManifestPath = @".wsgm\library.json";
    private static readonly object PublicationGate = new();

    /// <summary>Reads a complete strict manifest through the expected volume GUID, or throws without substituting empty inventory.</summary>
    public static StorageLibraryManifest Read(string volumeId, string? expectedLibraryId = null)
    {
        using var document = JsonDocument.Parse(ReadBytes(volumeId), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Version", out var writtenVersion)
            && writtenVersion.ValueKind == JsonValueKind.Number
            && writtenVersion.TryGetInt32(out var version) && version != 1)
        {
            throw new StorageLibraryUnsupportedVersionException(version);
        }

        CheckKnownFields(document.RootElement, typeof(StorageLibraryManifest));
        var manifest = document.RootElement.Deserialize(StorageLibraryJsonContext.Default.StorageLibraryManifest)
                       ?? throw new InvalidDataException("The library manifest is empty.");
        Validate(manifest, volumeId);
        if (expectedLibraryId is not null && !SameId(expectedLibraryId, manifest.Id))
        {
            throw new InvalidDataException("This storage contains a different library. Review it in Game Library.");
        }
        return manifest;
    }

    private static void CheckKnownFields(JsonElement element, Type type)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var metadata = StorageLibraryJsonContext.Default.GetTypeInfo(type)!;
        foreach (var property in element.EnumerateObject())
        {
            var known = metadata.Properties.FirstOrDefault(item => item.Name == property.Name);
            if (known is null)
            {
                throw new StorageLibraryUnsupportedVersionException(
                    $"This library contains an unsupported field '{property.Name}'. Update WSGM before using this card.");
            }
            if (known.PropertyType == typeof(StorageLibraryPath))
            {
                CheckKnownFields(property.Value, typeof(StorageLibraryPath));
            }
            else if (property.Value.ValueKind == JsonValueKind.Array
                     && (known.PropertyType == typeof(List<StorageLibraryPath>)
                         || known.PropertyType == typeof(List<StorageLibraryContent>)))
            {
                var itemType = known.PropertyType == typeof(List<StorageLibraryPath>)
                    ? typeof(StorageLibraryPath) : typeof(StorageLibraryContent);
                foreach (var item in property.Value.EnumerateArray())
                {
                    CheckKnownFields(item, itemType);
                }
            }
        }
    }

    private static byte[] ReadBytes(string volumeId)
    {
        ManagedContentStorage.RejectLibraryReparsePoints(volumeId, ManifestPath);
        return File.ReadAllBytes(ManagedContentStorage.ResolveLibraryPath(volumeId, ManifestPath));
    }

    /// <summary>Repairs a missing/damaged manifest only after explicit review/Apply of an accepted local binding.</summary>
    /// <remarks>Build inventory only from known managed records. Existing bytes are preserved in a unique sibling before replacement.</remarks>
    public static void Repair(StorageLibraryRecord library, StorageLibraryManifest manifest)
    {
        Validate(manifest, library.VolumeId);
        if (!library.RepairRequired || !SameId(library.Id, manifest.Id)
            || manifest.Revision != checked(library.ManifestRevision + 1))
        {
            throw new InvalidDataException("Review this library's repair and expected revision before applying.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, StorageLibraryJsonContext.Default.StorageLibraryManifest);
        lock (PublicationGate)
        {
            try
            {
                _ = Read(library.VolumeId);
                throw new InvalidOperationException("This manifest is valid. Recheck and publish a normal revision instead.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // Only explicit repair may replace malformed bytes, which are preserved below.
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                RequireMissingManifest(library.VolumeId);
            }
            byte[]? Snapshot()
            {
                try
                {
                    return ReadBytes(library.VolumeId);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    RequireMissingManifest(library.VolumeId);
                    return null;
                }
            }
            var original = Snapshot();
            Directory.CreateDirectory(ManagedContentStorage.ResolveLibraryPath(library.VolumeId, ".wsgm"));
            ManagedContentStorage.RejectLibraryReparsePoints(library.VolumeId, ".wsgm");
            if (original is not null)
            {
                var backup = ManagedContentStorage.ResolveLibraryPath(library.VolumeId,
                    @".wsgm\library.repair-" + Guid.NewGuid().ToString("N") + ".json");
                using var preserved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough);
                preserved.Write(original);
                preserved.Flush(flushToDisk: true);
            }
            AtomicFile.Write(ManagedContentStorage.ResolveLibraryPath(library.VolumeId, ManifestPath), stream =>
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                var current = Snapshot();
                if (original is null ? current is not null : current is null || !original.SequenceEqual(current))
                {
                    throw new InvalidDataException("The library changed during repair. Review it again.");
                }
                return true;
            }, durable: true);
        }
    }

    /// <summary>Atomically publishes the next generation; null revision permits creation only, never replacement of a lost known manifest.</summary>
    /// <remarks>The session owner must serialize inventory decisions and check cancellation before calling. An uncertain failure is never retried here.</remarks>
    public static void Publish(string volumeId, StorageLibraryManifest manifest, long? expectedRevision)
    {
        Validate(manifest, volumeId);
        if (manifest.Revision != (expectedRevision.HasValue ? checked(expectedRevision.Value + 1) : 1))
        {
            throw new InvalidDataException("The library manifest revision must advance by exactly one.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, StorageLibraryJsonContext.Default.StorageLibraryManifest);
        lock (PublicationGate)
        {
            AssertGeneration(volumeId, manifest.Id, expectedRevision);
            var directory = ManagedContentStorage.ResolveLibraryPath(volumeId, ".wsgm");
            Directory.CreateDirectory(directory);
            ManagedContentStorage.RejectLibraryReparsePoints(volumeId, ".wsgm");
            var target = ManagedContentStorage.ResolveLibraryPath(volumeId, ManifestPath);
            AtomicFile.Write(target, stream =>
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                AssertGeneration(volumeId, manifest.Id, expectedRevision);
                return true;
            }, durable: true);
        }
    }

    private static void AssertGeneration(string volumeId, string libraryId, long? revision)
    {
        if (revision.HasValue)
        {
            if (Read(volumeId, libraryId).Revision != revision.Value)
            {
                throw new InvalidDataException("The library changed while it was being checked. Recheck before applying.");
            }
            return;
        }

        RequireMissingManifest(volumeId);
    }

    /// <summary>Requires a confirmed absence of the manifest on readable storage before creating one.</summary>
    private static void RequireMissingManifest(string volumeId)
    {
        switch (InspectPath(volumeId, ManifestPath))
        {
            case ContentPathState.Missing:
                return;
            case ContentPathState.Present:
                throw new InvalidDataException("A library manifest already exists. Review it before applying.");
            default:
                throw new IOException("The storage could not be read. Reconnect it and try again.");
        }
    }

    /// <summary>Queries one contained path through the shared probe, rejecting reparse points on the card.</summary>
    /// <returns>Missing only below a readable ancestor on this volume; access failures stay Unreadable.</returns>
    public static ContentPathState InspectPath(string volumeId, string relativePath, bool directory = false,
        ManagedContentCheckContext? context = null)
    {
        try
        {
            return ManagedContentStorage.Probe(new ManagedContentPath
            {
                VolumeId = volumeId, RelativePath = relativePath, Directory = directory,
                AbsolutePath = ManagedContentStorage.ResolveLibraryPath(volumeId, relativePath)
            }, context, rejectReparsePoints: true);
        }
        catch (InvalidDataException)
        {
            return ContentPathState.Unreadable;
        }
    }

    /// <summary>Finds a unique existing portable recipe; a conflict throws rather than guessing an ID.</summary>
    public static StorageLibraryContent? Match(StorageLibraryManifest manifest, ManagedContentRecord content)
    {
        if (content.LibraryId.Length > 0 && !SameId(content.LibraryId, manifest.Id))
        {
            throw new InvalidDataException("A content record belongs to another library.");
        }
        var recipe = FromManaged(content, content.BackingPath.VolumeId);
        var matches = manifest.Entries.Where(entry => SameRecipe(entry, recipe)
            && (!recipe.RequiresLocalRecipe || entry.Id == content.Id)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException("Several library entries match this launch recipe. Review the conflict.")
        };
    }

    /// <summary>Whether two card entries are the same in every field a manifest stores.</summary>
    /// <remarks>Value equality for publication decisions: an unchanged entry is never rewritten.</remarks>
    public static bool SameEntry(StorageLibraryContent left, StorageLibraryContent right)
    {
        static bool SamePath(StorageLibraryPath? a, StorageLibraryPath? b) => a is null || b is null
            ? a is null && b is null
            : a.Directory == b.Directory && string.Equals(a.RelativePath, b.RelativePath, StringComparison.Ordinal);
        return left.Id == right.Id && left.Name == right.Name && left.SourceKind == right.SourceKind
               && left.SourceName == right.SourceName && left.SystemId == right.SystemId
               && SamePath(left.BackingPath, right.BackingPath) && SamePath(left.SourceRoot, right.SourceRoot)
               && left.RequiredPaths.Count == right.RequiredPaths.Count
               && left.RequiredPaths.Zip(right.RequiredPaths).All(pair => SamePath(pair.First, pair.Second))
               && SamePath(left.Program, right.Program) && SamePath(left.WorkingDirectory, right.WorkingDirectory)
               && left.RawArguments == right.RawArguments && left.RequiresLocalRecipe == right.RequiresLocalRecipe;
    }

    private static bool SameRecipe(StorageLibraryContent left, StorageLibraryContent right)
    {
        static bool SamePath(StorageLibraryPath? a, StorageLibraryPath? b) => a is null || b is null
            ? a is null && b is null
            : a.Directory == b.Directory && string.Equals(a.RelativePath.Replace('/', '\\'),
                b.RelativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        return left.SourceKind == right.SourceKind && SamePath(left.BackingPath, right.BackingPath)
            && (left.SourceKind == LibrarySourceKind.Rom
                ? EmulatorStorage.NormalizeSystemId(left.SystemId) == EmulatorStorage.NormalizeSystemId(right.SystemId)
                : left.RequiresLocalRecipe == right.RequiresLocalRecipe && SamePath(left.Program, right.Program)
                    && SamePath(left.WorkingDirectory, right.WorkingDirectory) && left.RawArguments == right.RawArguments);
    }

    private static bool SameId(string left, string right) => Guid.TryParse(left, out var a)
        && Guid.TryParse(right, out var b) && a == b;

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("The library manifest contains duplicate properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateProperties(child);
            }
        }
    }

    /// <summary>Validates complete schema and contained paths before any inventory is accepted.</summary>
    public static void Validate(StorageLibraryManifest manifest, string volumeId)
    {
        _ = ManagedContentStorage.ResolveLibraryPath(volumeId, "");
        if (manifest.Version != 1)
        {
            throw new StorageLibraryUnsupportedVersionException(manifest.Version);
        }

        if (!Guid.TryParseExact(manifest.Id, "D", out var id) || id == Guid.Empty
            || manifest.Name is null || manifest.Revision < 1 || manifest.Entries is null)
        {
            throw new InvalidDataException("The library manifest schema, UUID or revision is invalid.");
        }
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (entry is null || entry.Id is null || entry.Id.Length != 32 || !entry.Id.All(Uri.IsHexDigit)
                || !ids.Add(entry.Id) || entry.Name is null || entry.SourceName is null || entry.SystemId is null
                || entry.RawArguments is null || entry.RequiredPaths is null || entry.BackingPath is null
                || entry.SourceKind is not (LibrarySourceKind.Rom or LibrarySourceKind.Folder or LibrarySourceKind.Manual)
                || entry.SourceKind == LibrarySourceKind.Rom && entry.SystemId.Length == 0
                || entry.SourceKind != LibrarySourceKind.Rom && !entry.RequiresLocalRecipe
                    && (entry.Program is null || entry.WorkingDirectory is null))
            {
                throw new InvalidDataException("A library entry has invalid identity, source or launch fields.");
            }
            foreach (var path in new[] { entry.BackingPath, entry.SourceRoot, entry.Program, entry.WorkingDirectory }
                         .Where(path => path is not null).Concat(entry.RequiredPaths))
            {
                if (path is null || path.RelativePath is null)
                {
                    throw new InvalidDataException("A library entry has an incomplete path.");
                }
                _ = ManagedContentStorage.ResolveLibraryPath(volumeId, path.RelativePath);
            }
        }
    }

    /// <summary>Projects local metadata to a portable recipe without leaking external dependency paths.</summary>
    public static StorageLibraryContent FromManaged(ManagedContentRecord content, string volumeId, string sourceName = "")
    {
        _ = ManagedContentStorage.ResolveLibraryPath(volumeId, "");
        if (ManagedContentStorage.RecordDefect(content) is { } defect)
        {
            throw new InvalidDataException(defect);
        }
        StorageLibraryPath? Portable(ManagedContentPath path) => path.VolumeId.Equals(volumeId, StringComparison.OrdinalIgnoreCase)
            ? new StorageLibraryPath
            {
                RelativePath = ManagedContentStorage.NormalizeLocalRelativePath(path.RelativePath),
                Directory = path.Directory
            }
            : null;
        var backing = Portable(content.BackingPath)
                      ?? throw new InvalidDataException("The backing content belongs to different storage.");
        var program = Portable(content.Program);
        var directory = Portable(content.WorkingDirectory);
        return new StorageLibraryContent
        {
            Id = content.Id,
            Name = content.Name,
            SourceKind = content.SourceKind,
            SourceName = sourceName,
            SystemId = content.SystemId,
            BackingPath = backing,
            SourceRoot = Portable(content.SourceRoot),
            RequiredPaths = content.RequiredPaths.Select(path => Portable(path)
                ?? throw new InvalidDataException("A backing companion is outside this storage.")).ToList(),
            Program = content.SourceKind == LibrarySourceKind.Rom ? null : program,
            WorkingDirectory = content.SourceKind == LibrarySourceKind.Rom ? null : directory,
            RawArguments = content.SourceKind == LibrarySourceKind.Rom ? "" : content.RawArguments.Trim(),
            RequiresLocalRecipe = content.SourceKind != LibrarySourceKind.Rom && (program is null || directory is null)
        };
    }

    /// <summary>Materializes review metadata using authoritative manifest IDs.</summary>
    /// <remarks>The record launches only once the device's binding for this library names the same volume.</remarks>
    public static ManagedContentRecord ToManaged(StorageLibraryContent entry, StorageLibraryRecord library,
        string sourceId, ManagedContentRecord? localRecipe = null)
    {
        ManagedContentPath Bind(StorageLibraryPath path) => new()
        {
            VolumeId = library.VolumeId, RelativePath = path.RelativePath, Directory = path.Directory,
            AbsolutePath = ManagedContentStorage.ResolveLibraryPath(library.VolumeId, path.RelativePath)
        };
        var content = localRecipe?.Copy() ?? new ManagedContentRecord();
        if (localRecipe is not null && (localRecipe.Id != entry.Id
            || !SameId(localRecipe.LibraryId, library.Id)
            || !localRecipe.BackingPath.VolumeId.Equals(library.VolumeId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The local command recipe belongs to another library or title.");
        }
        content.Id = entry.Id;
        content.LibraryId = library.Id;
        content.SourceId = sourceId;
        content.SourceKey = entry.Id;
        content.Name = entry.Name;
        content.Location = library.Name;
        content.SourceKind = entry.SourceKind;
        content.SystemId = entry.SystemId;
        content.BackingPath = Bind(entry.BackingPath);
        content.SourceRoot = entry.SourceRoot is { } root ? Bind(root) : new ManagedContentPath { Directory = true };
        content.RequiredPaths = entry.RequiredPaths.Select(Bind).ToList();
        if (entry.SourceKind == LibrarySourceKind.Rom)
        {
            if (localRecipe is null || EmulatorStorage.NormalizeSystemId(localRecipe.SystemId)
                != EmulatorStorage.NormalizeSystemId(entry.SystemId))
            {
                content.FollowSystemPreference = true;
                content.EmulatorInstallationId = "";
                content.CoreId = "";
                content.Arguments = [];
            }
        }
        else if (!entry.RequiresLocalRecipe)
        {
            content.Program = Bind(entry.Program!);
            content.WorkingDirectory = Bind(entry.WorkingDirectory!);
            content.RawArguments = entry.RawArguments;
            content.Arguments = [];
        }
        return content;
    }
}
