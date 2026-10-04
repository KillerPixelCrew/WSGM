using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WSGM.Core;

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
    internal string[] Assets { get; init; } = [];
}

/// <summary>Reads Audio Loader manifests and installs their assets in WSGM's per-user content folder.</summary>
internal sealed class SoundPackLibrary(string root)
{
    internal string Root { get; } = Path.GetFullPath(root);
    internal static string DefaultRoot(UserDataContext context) => Path.Combine(context.Root, "sounds");

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

    internal SoundPack ReadPack(string id)
    {
        var directory = PackPath(id);
        var manifest = Path.Combine(directory, "pack.json");
        CheckPath(Root, manifest);
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var json = document.RootElement;
        if (json.TryGetProperty("manifest_version", out var version) && version.GetInt32() > 3)
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
        return new SoundPack(id, name, Text(json, "author"), Text(json, "version"), Text(json, "description"),
            File.Exists(source) ? File.ReadAllText(source) : null, null, mappings, ignore)
        {
            Assets = mappings.Values.SelectMany(files => files)
                .Concat(Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>())
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".wav" or ".mp3" or ".ogg" or ".m4a")
                .Distinct(StringComparer.Ordinal).ToArray()
        };
    }

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

    internal string AssetPath(string id, string file)
    {
        var directory = PackPath(id);
        var path = Path.GetFullPath(Path.Combine(directory, file));
        CheckPath(directory, path);
        return path;
    }

    internal Dictionary<string, string[]> BuildOverrides(SoundPack pack, IReadOnlyCollection<string> resources,
        out string compatibility)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var missing = 0;
        foreach (var resource in resources)
        {
            if (pack.Ignore.Contains(resource))
            {
                continue;
            }

            var files = pack.Mappings.TryGetValue(resource, out var mapped) ? mapped : [resource];
            var urls = new List<string>();
            foreach (var file in files)
            {
                var path = AssetPath(pack.Id, file);
                if (!File.Exists(path))
                {
                    missing++;
                    continue;
                }

                // An empty file is no sound. Any size plays: the toolkit delivers the set to Steam in parts.
                if (new FileInfo(path).Length == 0)
                {
                    missing++;
                    continue;
                }

                var mime = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg",
                    _ => null
                };
                if (mime is null)
                {
                    missing++;
                    continue;
                }

                urls.Add($"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}");
            }

            if (urls.Count > 0)
            {
                result.Add(resource, urls.ToArray());
            }
        }

        var unknown = pack.Mappings.Keys.Count(key => !resources.Contains(key, StringComparer.Ordinal));
        compatibility =
            $"{result.Count} supported resources; {missing} missing or unsupported assets; {unknown} unknown mappings. Unmatched events use Steam defaults.";
        return result;
    }

    internal string Install(Stream archive, string? storeId = null)
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
            var identity = storeId ?? stagedPack.Name;
            var id = (storeId is null ? "local-" : "store-")
                     + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
            if (storeId is not null)
            {
                File.WriteAllText(Path.Combine(content, ".wsgm-store-id"), storeId);
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

    internal void Delete(string id)
    {
        var path = PackPath(id);
        ValidateTree(path);
        Directory.Delete(path, true);
    }

    private static void ValidateTree(string path)
    {
        CheckPath(path, path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            CheckPath(path, child);
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
