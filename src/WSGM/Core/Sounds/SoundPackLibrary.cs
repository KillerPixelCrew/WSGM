using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>Installed Audio Loader metadata and resource mappings, or a visible manifest-read failure.</summary>
/// <param name="Id">Local installation-folder identity.</param>
/// <param name="Name">Display name.</param>
/// <param name="Author">Manifest author text.</param>
/// <param name="Version">Manifest version text.</param>
/// <param name="Description">Manifest description.</param>
/// <param name="StoreId">Store listing identity, or null for a local archive.</param>
/// <param name="Error">Manifest-read failure, or null when accepted.</param>
/// <param name="Mappings">Steam resource names mapped to ordered relative sound paths.</param>
/// <param name="Ignore">Resources intentionally left using Steam defaults.</param>
internal sealed record SoundPack(
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    string? StoreId,
    string? Error,
    IReadOnlyDictionary<string, string[]> Mappings,
    IReadOnlySet<string> Ignore)
{
    /// <summary>Distinct supported preview and sound paths, relative to the pack directory.</summary>
    internal string[] Assets { get; init; } = [];
    /// <summary>Author-supplied manifest identity, when present.</summary>
    internal string ManifestId { get; init; } = "";
    /// <summary>Repository/source text from the manifest or installation sidecar.</summary>
    internal string Source { get; init; } = "";
    /// <summary>Most recent comparison against Steam resources, when the caller supplied one.</summary>
    internal SoundPackCompatibility? Compatibility { get; init; }
}

/// <summary>Pack coverage against one observed Steam resource vocabulary.</summary>
/// <param name="SupportedResources">Resources with at least one usable file.</param>
/// <param name="MissingResources">Resources with no usable file.</param>
/// <param name="IgnoredResources">Resources explicitly excluded by the pack.</param>
/// <param name="UnknownMappings">Manifest mappings absent from the current Steam vocabulary.</param>
/// <param name="AssetProblems">Missing, empty, unsupported or unreadable asset details.</param>
internal sealed record SoundPackCompatibility(
    string[] SupportedResources,
    string[] MissingResources,
    string[] IgnoredResources,
    string[] UnknownMappings,
    string[] AssetProblems)
{
    /// <summary>Counts and fallback behavior suitable for the pack-details surface.</summary>
    internal string Summary =>
        $"{SupportedResources.Length} mapped resources; {MissingResources.Length} missing resources; {IgnoredResources.Length} ignored resources; {AssetProblems.Length} missing or unsupported assets; {UnknownMappings.Length} unknown mappings. Unmatched events use Steam defaults.";
}

/// <summary>Reads Audio Loader manifests and installs their assets in WSGM's per-user content folder.</summary>
/// <param name="root">Local sound-pack directory; paths are constrained beneath this root.</param>
internal sealed class SoundPackLibrary(string root)
{
    /// <summary>Absolute library root.</summary>
    internal string Root { get; } = Path.GetFullPath(root);

    /// <summary>Resolves the sound-pack directory for one WSGM user context.</summary>
    /// <param name="context">The current user's data root.</param>
    /// <returns>The sounds child directory without creating it.</returns>
    internal static string DefaultRoot(UserDataContext context)
    {
        return Path.Combine(context.Root, "sounds");
    }

    /// <summary>Lists installed packs, retaining per-pack read failures as visible error entries.</summary>
    /// <returns>Packs sorted by display name; library-root access failures still propagate.</returns>
    internal SoundPack[] Read()
    {
        Directory.CreateDirectory(Root);
        CheckPath(Root, Root);
        return Directory.EnumerateDirectories(Root).Where(path => !Path.GetFileName(path).StartsWith('.'))
            .Select(path =>
            {
                var id = Path.GetFileName(path);
                try
                {
                    return ReadPack(id);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                                  or InvalidOperationException or ArgumentException)
                {
                    return new SoundPack(id, id, "", "", "", null, error.Message,
                        new Dictionary<string, string[]>(), new HashSet<string>());
                }
            }).OrderBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Reads and validates one UI sound manifest and its constrained asset paths.</summary>
    /// <param name="id">Local installation-folder identity.</param>
    /// <returns>Parsed metadata and supported asset paths; missing mapped files are assessed separately.</returns>
    /// <exception cref="InvalidDataException">The manifest, version or path shape is unsupported.</exception>
    internal SoundPack ReadPack(string id)
    {
        var directory = PackPath(id);
        var manifest = Path.Combine(directory, "pack.json");
        CheckPath(Root, manifest);
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The sound-pack manifest must be an object.");
        }

        if (json.TryGetProperty("manifest_version", out var version)
            && (!version.TryGetInt32(out var manifestVersion) || manifestVersion is < 1 or > 3))
        {
            throw new InvalidDataException("This pack requires a newer Audio Loader manifest.");
        }

        if (json.TryGetProperty("music", out var music) && music.GetBoolean())
        {
            throw new InvalidDataException("Background music packs are not UI sound packs.");
        }

        var name = Text(json, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("The sound pack has no name.");
        }

        var mappings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (json.TryGetProperty("mappings", out var map))
        {
            foreach (var item in map.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Mappings must be arrays of filenames.");
                }

                var files = item.Value.EnumerateArray().Select(value => value.GetString()
                                                                        ?? throw new InvalidDataException(
                                                                            "A mapping has no filename.")).ToArray();
                if (files.Length == 0)
                {
                    throw new InvalidDataException("A mapping needs at least one sound.");
                }

                foreach (var file in files)
                {
                    AssetPath(id, file);
                }

                mappings.Add(item.Name, files);
            }
        }

        var ignore = json.TryGetProperty("ignore", out var ignored)
            ? ignored.EnumerateArray().Select(value => value.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var source = Path.Combine(directory, ".wsgm-store-id");
        CheckPath(Root, source);
        var repositorySource = Path.Combine(directory, ".wsgm-source");
        CheckPath(Root, repositorySource);
        var repository = Text(json, "source");
        if (repository.Length == 0)
        {
            repository = Text(json, "repository");
        }

        if (repository.Length == 0 && File.Exists(repositorySource))
        {
            repository = File.ReadAllText(repositorySource);
        }

        var previewAssets = new List<string>();
        if (json.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.String)
        {
            previewAssets.Add(preview.GetString()!);
        }

        if (json.TryGetProperty("previews", out var previews))
        {
            previewAssets.AddRange(previews.EnumerateArray().Select(value => value.GetString()
                                                                             ?? throw new InvalidDataException(
                                                                                 "A preview has no filename.")));
        }

        foreach (var asset in previewAssets)
        {
            AssetPath(id, asset);
        }

        return new SoundPack(id, name, Text(json, "author"), Text(json, "version"), Text(json, "description"),
            File.Exists(source) ? File.ReadAllText(source) : null, null, mappings, ignore)
        {
            ManifestId = Text(json, "id"),
            Source = repository,
            Assets = previewAssets.Concat(mappings.Values.SelectMany(files => files))
                .Concat(EnumerateAssets(directory).Select(path => Path.GetRelativePath(directory, path)))
                .Where(file => Mime(file) is not null)
                .Select(file => Path.GetRelativePath(directory, AssetPath(id, file))
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    /// <summary>Resolves a pack identity under the library and rejects invalid or redirected paths.</summary>
    /// <param name="id">Single folder-name identity.</param>
    /// <returns>The constrained path; existence is not required.</returns>
    internal string PackPath(string id)
    {
        if (id.Length == 0 || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("Invalid sound-pack identity.");
        }

        var path = Path.Combine(Root, id);
        CheckPath(Root, path);
        return path;
    }

    /// <summary>Resolves an asset without allowing escape from its pack or traversal through reparse points.</summary>
    /// <param name="id">Installed pack identity.</param>
    /// <param name="file">Manifest asset path.</param>
    /// <returns>The constrained absolute path; existence is not required.</returns>
    internal string AssetPath(string id, string file)
    {
        var directory = PackPath(id);
        var path = Path.GetFullPath(Path.Combine(directory, file));
        CheckPath(directory, path);
        return path;
    }

    /// <summary>Builds data-URL overrides for usable assets while leaving unmatched events at Steam defaults.</summary>
    /// <param name="pack">Accepted installed pack.</param>
    /// <param name="resources">Current Steam resource names.</param>
    /// <param name="compatibility">Coverage and asset-failure counts for this read.</param>
    /// <returns>Only resources with at least one readable, nonempty supported sound.</returns>
    internal Dictionary<string, string[]> BuildOverrides(SoundPack pack, IReadOnlyCollection<string> resources,
        out string compatibility)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var resource in resources)
        {
            if (pack.Ignore.Contains(resource))
            {
                continue;
            }

            var urls = new List<string>();
            foreach (var path in ResolveAssets(pack, resource, problems))
            {
                try
                {
                    // The file can disappear after compatibility inspection. One failed asset must
                    // leave unrelated events usable, and an empty read must retain the stock event.
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length > 0)
                    {
                        urls.Add($"data:{Mime(path)};base64,{Convert.ToBase64String(bytes)}");
                    }
                    else
                    {
                        problems.Add($"{resource}: {Path.GetRelativePath(PackPath(pack.Id), path)} is empty.");
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{resource}: {Path.GetRelativePath(PackPath(pack.Id), path)}: {error.Message}");
                }
            }

            if (urls.Count > 0)
            {
                result.Add(resource, urls.ToArray());
            }
        }

        var unknown = pack.Mappings.Keys.Count(key => !resources.Contains(key, StringComparer.Ordinal));
        compatibility =
            $"{result.Count} mapped resources; {problems.Count} missing or unsupported assets; {unknown} unknown mappings. Unmatched events use Steam defaults.";
        return result;
    }

    /// <summary>Checks resource coverage, asset existence, extension and nonzero length without decoding audio.</summary>
    /// <param name="pack">Accepted installed pack.</param>
    /// <param name="resources">Current Steam resource names.</param>
    /// <returns>Coverage and path problems; files may change before overrides are read.</returns>
    internal SoundPackCompatibility InspectCompatibility(SoundPack pack, IReadOnlyCollection<string> resources)
    {
        var supported = new List<string>();
        var missing = new List<string>();
        var ignored = new List<string>();
        var problems = new List<string>();
        foreach (var resource in resources)
        {
            if (pack.Ignore.Contains(resource))
            {
                ignored.Add(resource);
            }
            else if (ResolveAssets(pack, resource, problems).Any())
            {
                supported.Add(resource);
            }
            else
            {
                missing.Add(resource);
            }
        }

        return new SoundPackCompatibility(supported.ToArray(), missing.ToArray(), ignored.ToArray(),
            pack.Mappings.Keys.Where(key => !resources.Contains(key, StringComparer.Ordinal)).ToArray(),
            problems.ToArray());
    }

    private List<string> ResolveAssets(SoundPack pack, string resource, List<string> problems)
    {
        var files = pack.Mappings.TryGetValue(resource, out var mapped) ? mapped : [resource];
        var paths = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var path = AssetPath(pack.Id, file);
                var problem = !File.Exists(path) ? "missing"
                    : Mime(path) is null ? "unsupported format"
                    : new FileInfo(path).Length == 0 ? "empty" : null;
                if (problem is null)
                {
                    paths.Add(path);
                }
                else
                {
                    problems.Add($"{resource}: {file} ({problem}).");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{resource}: {file}: {error.Message}");
            }
        }

        return paths;
    }

    private static string? Mime(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg",
            _ => null
        };
    }

    /// <summary>Stages and validates a bounded ZIP, then replaces the matching installed pack with rollback on move failure.</summary>
    /// <param name="archive">Readable archive borrowed for this call; left open.</param>
    /// <param name="storeId">Store identity, or null for a local archive.</param>
    /// <param name="source">Optional store-source attribution.</param>
    /// <returns>The stable local installation id.</returns>
    /// <remarks>Rejects expanded content over 64 MiB, links and ambiguous manifests. Cleanup failures propagate.</remarks>
    internal string Install(Stream archive, string? storeId = null, string? source = null)
    {
        Directory.CreateDirectory(Root);
        CheckPath(Root, Root);
        var stage = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, true);
            // The one bound kept: the expanded bytes, against a zip bomb. It refuses the archive whole.
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                expanded += entry.Length;
                if (expanded > 64 * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new InvalidDataException("The archive is too large or contains symbolic links.");
                }

                CheckPath(stage, Path.GetFullPath(Path.Combine(stage, entry.FullName)));
            }

            zip.ExtractToDirectory(stage);
            var manifests = Directory.GetFiles(stage, "pack.json", SearchOption.AllDirectories);
            if (manifests.Length != 1)
            {
                throw new InvalidDataException("The archive must contain exactly one pack.json.");
            }

            var content = Path.GetDirectoryName(manifests[0])!;
            var stagedLibrary = new SoundPackLibrary(Path.GetDirectoryName(content)!);
            var stagedPack = stagedLibrary.ReadPack(Path.GetFileName(content));
            var identity = storeId ?? (stagedPack.ManifestId.Length > 0 ? stagedPack.ManifestId : stagedPack.Name);
            var id = (storeId is null ? "local-" : "store-")
                     + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
            if (storeId is null)
            {
                // Keep an older name-based installation's identity when its author adds an id.
                var existing = Read().FirstOrDefault(pack => pack.StoreId is null && pack.Error is null
                    && ((stagedPack.ManifestId.Length > 0 && pack.ManifestId == stagedPack.ManifestId)
                        || (pack.ManifestId.Length == 0 && pack.Name == stagedPack.Name)));
                id = existing?.Id ?? id;
            }

            if (storeId is not null)
            {
                File.WriteAllText(Path.Combine(content, ".wsgm-store-id"), storeId);
                if (source is { Length: > 0 })
                {
                    File.WriteAllText(Path.Combine(content, ".wsgm-source"), source);
                }
            }
            else
            {
                File.Delete(Path.Combine(content, ".wsgm-store-id"));
                File.Delete(Path.Combine(content, ".wsgm-source"));
            }

            var destination = PackPath(id);
            var backup = Path.Combine(Root, ".backup-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(destination))
            {
                ValidateTree(destination);
                Directory.Move(destination, backup);
            }

            try
            {
                Directory.Move(content, destination);
            }
            catch
            {
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }

                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, true);
            }

            return id;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
        }
    }

    /// <summary>Validates and recursively removes one installed pack directory.</summary>
    /// <param name="id">Local pack identity; callers must reconcile any active sound selection separately.</param>
    internal void Delete(string id)
    {
        var path = PackPath(id);
        ValidateTree(path);
        Directory.Delete(path, true);
    }

    private static void ValidateTree(string path)
    {
        CheckPath(path, path);
        foreach (var child in EnumerateAssets(path))
        {
            CheckPath(path, child);
        }
    }

    private static IEnumerable<string> EnumerateAssets(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckPath(root, child);
                if (Directory.Exists(child))
                {
                    pending.Push(child);
                }
                else
                {
                    yield return child;
                }
            }
        }
    }

    private static string Text(JsonElement json, string key)
    {
        return json.TryGetProperty(key, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static void CheckPath(string rootPath, string path)
    {
        var rootFull = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The pack path escapes its folder.");
        }

        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Sound packs cannot contain redirected paths.");
            }

            if (current.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }
}
