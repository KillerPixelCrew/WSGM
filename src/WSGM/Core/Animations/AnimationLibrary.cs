using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WSGM.Core;

/// <summary>WSGM's animations: the movies downloaded from the repository and the files the user brought.</summary>
/// <remarks>
///     <para>
///         Content and assignment are kept apart, as Animation Changer keeps them: the library holds
///         the movies under <c>downloads\{id}.webm</c> with their listings in <c>downloads.json</c>,
///         and under <c>custom\</c> whatever <c>.webm</c> the user put or copied there; which movie
///         plays at boot is the configuration's. That is what makes a shuffle and a return to stock
///         possible without touching the files.
///     </para>
///     <para>
///         Every read starts from the folder, so a file removed by hand disappears from the library
///         and a file dropped into <c>custom</c> appears in it.
///     </para>
/// </remarks>
public sealed class AnimationLibrary
{
    private const string DownloadsFolder = "downloads";
    private const string CustomFolder = "custom";
    private const string CatalogFile = "downloads.json";
    private readonly Lock _gate = new();
    private List<AnimationEntry> _entries = [];

    /// <summary>Creates the library over one folder.</summary>
    /// <param name="root">The folder.</param>
    public AnimationLibrary(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    /// <summary>WSGM's animations folder.</summary>
    public static string DefaultRoot => Path.Combine(Log.Directory, "animations");

    /// <summary>The folder.</summary>
    public string Root { get; }

    /// <summary>The folder a brought file is copied into, and where one can be put by hand.</summary>
    public string CustomRoot => Path.Combine(Root, CustomFolder);

    /// <summary>Every animation, downloads first, then brought files, each in name order.</summary>
    public IReadOnlyList<AnimationEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    /// <summary>The last problem reading the folder, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Reads the folder.</summary>
    public void Load()
    {
        List<AnimationEntry> entries = [];
        string? error = null;
        try
        {
            var catalog = Path.Combine(Root, CatalogFile);
            if (File.Exists(catalog))
            {
                var listings = JsonSerializer.Deserialize(File.ReadAllText(catalog),
                    AnimationJsonContext.Default.ListAnimationListing) ?? [];
                foreach (var listing in listings)
                {
                    if (!AnimationRepoClient.ValidId(listing.Id))
                    {
                        continue;
                    }

                    var path = DownloadPath(listing.Id);
                    if (File.Exists(path))
                    {
                        entries.Add(new AnimationEntry(listing.Id, listing.Name, path, listing));
                    }
                }
            }

            entries.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(CustomRoot))
            {
                foreach (var file in Directory.EnumerateFiles(CustomRoot, "*.webm")
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    entries.Add(new AnimationEntry(AnimationEntry.CustomPrefix + Path.GetFileName(file), name,
                        file, null));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
        }

        lock (_gate)
        {
            _entries = entries;
            LoadError = error;
        }
    }

    /// <summary>One animation by id, or null.</summary>
    /// <param name="id">The id.</param>
    public AnimationEntry? Find(string id)
    {
        return Entries.FirstOrDefault(entry => entry.Id == id);
    }

    /// <summary>Where a download of this id is written before the library adopts it.</summary>
    /// <param name="id">A listing id, already checked with <see cref="AnimationRepoClient.ValidId" />.</param>
    /// <returns>The staging file's path, beside the movie it becomes.</returns>
    public string StagingPath(string id)
    {
        return DownloadPath(id) + ".part";
    }

    /// <summary>Keeps a downloaded movie with its listing, moving the staged file into place.</summary>
    /// <param name="listing">The listing.</param>
    /// <param name="staged">The complete download at <see cref="StagingPath" />.</param>
    /// <returns>Null, or why it could not be kept.</returns>
    public string? Adopt(AnimationListing listing, string staged)
    {
        if (!AnimationRepoClient.ValidId(listing.Id))
        {
            return "The animation's id is not one a file can be named by.";
        }

        try
        {
            File.Move(staged, DownloadPath(listing.Id), true);
            var listings = Entries.Where(entry => entry.Listing is not null && entry.Id != listing.Id)
                .Select(entry => entry.Listing!).Append(listing).ToList();
            WriteCatalog(listings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        Load();
        return null;
    }

    /// <summary>Copies a file the user chose into the library; <see cref="Load" /> then lists it.</summary>
    /// <param name="sourcePath">The file.</param>
    /// <returns>The new entry's id, or the reason it could not be copied.</returns>
    /// <remarks>
    ///     The copy does not reread the folder, so an owner can copy outside its own lock and reread
    ///     under it, where no other change to the library can interleave.
    /// </remarks>
    public (string? Id, string? Error) Import(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return (null, "The file does not exist.");
            }

            if (!string.Equals(Path.GetExtension(sourcePath), ".webm", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "Steam plays WebM movies; choose a .webm file.");
            }

            if (new FileInfo(sourcePath).Length > AnimationRepoClient.MaximumMovieBytes)
            {
                return (null, "The movie is larger than the 64 MB safety limit.");
            }

            Directory.CreateDirectory(CustomRoot);
            var name = Path.GetFileName(sourcePath);
            var target = Path.Combine(CustomRoot, name);
            File.Copy(sourcePath, target, true);
            return (AnimationEntry.CustomPrefix + name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Removes an animation and its file.</summary>
    /// <param name="id">The id.</param>
    /// <returns>Null, or why it could not be removed.</returns>
    public string? Remove(string id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return "That animation is not in the library.";
        }

        try
        {
            if (File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
            }

            if (entry.Listing is not null)
            {
                WriteCatalog(Entries.Where(candidate => candidate.Listing is not null && candidate.Id != id)
                    .Select(candidate => candidate.Listing!).ToList());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        Load();
        return null;
    }

    private string DownloadPath(string id)
    {
        return Path.Combine(Root, DownloadsFolder, id + ".webm");
    }

    private void WriteCatalog(List<AnimationListing> listings)
    {
        Directory.CreateDirectory(Root);
        AtomicFile.WriteText(Path.Combine(Root, CatalogFile),
            JsonSerializer.Serialize(listings, AnimationJsonContext.Default.ListAnimationListing), false);
    }
}

/// <summary>Serializer metadata for the library's catalog.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<AnimationListing>))]
internal sealed partial class AnimationJsonContext : JsonSerializerContext;
