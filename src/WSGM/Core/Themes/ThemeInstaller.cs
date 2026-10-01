using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Installs a theme from the store into the themes folder, as CSS Loader does.</summary>
/// <remarks>
///     Mirrors <c>install</c> in <c>css_remoteinstall.py</c> (b1bc683): the theme's details are read,
///     a manifest newer than the loader reads is refused, the package is downloaded and unpacked over
///     the themes folder, and every dependency the store lists that is not already installed is
///     installed the same way. The package is a zip holding the theme's folder, so unpacking it beside
///     the others is the install; an entry that would land outside the folder fails the whole unpack.
/// </remarks>
public sealed class ThemeInstaller
{
    private readonly ThemeStoreClient _client;
    private readonly string _root;

    /// <summary>Creates the installer.</summary>
    /// <param name="client">The store.</param>
    /// <param name="root">The themes folder.</param>
    public ThemeInstaller(ThemeStoreClient client, string root)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _client = client;
        _root = root;
    }

    /// <summary>Installs one theme and the dependencies it lacks.</summary>
    /// <param name="id">The store id.</param>
    /// <param name="localNames">The names of the themes already installed.</param>
    /// <param name="cancellationToken">Cancels the install between steps.</param>
    /// <returns>The names of the themes installed, the asked-for one first.</returns>
    /// <exception cref="ThemeStoreException">The store refused, or the package could not be unpacked.</exception>
    public async Task<IReadOnlyList<string>> InstallAsync(
        string id, IReadOnlyCollection<string> localNames, CancellationToken cancellationToken)
    {
        HashSet<string> local = new(localNames, StringComparer.Ordinal);
        List<string> installed = [];
        await InstallAsync(id, local, installed, 0, cancellationToken).ConfigureAwait(false);
        return installed;
    }

    private async Task InstallAsync(
        string id, HashSet<string> local, List<string> installed, int depth, CancellationToken cancellationToken)
    {
        if (depth > 8)
        {
            throw new ThemeStoreException("The theme's dependencies nest too deeply.");
        }

        var details = await _client.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (details.Summary.ManifestVersion > ThemeManifest.SupportedVersion)
        {
            throw new ThemeStoreException(
                "Manifest version of themedb entry is unsupported by this version of CSS_Loader");
        }

        if (details.Summary.DownloadId is not { Length: > 0 } downloadId)
        {
            throw new ThemeStoreException("The theme store lists no package for this theme.");
        }

        using var package = await _client.DownloadBlobAsync(downloadId, cancellationToken).ConfigureAwait(false);
        Log.Info($"Themes: unpacking '{details.Summary.Name}' ({package.Length} bytes) into {_root}.");
        Unpack(package, _root);
        local.Add(details.Summary.Name);
        installed.Add(details.Summary.Name);

        foreach (var dependency in details.Dependencies.Where(dependency => !local.Contains(dependency.Name)))
        {
            await InstallAsync(dependency.Id, local, installed, depth + 1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Unpacks a package over the themes folder.</summary>
    /// <param name="package">The zip.</param>
    /// <param name="root">The themes folder.</param>
    /// <exception cref="ThemeStoreException">The zip could not be read or would write outside the folder.</exception>
    public static void Unpack(Stream package, string root)
    {
        root = Path.GetFullPath(root);
        var id = Guid.NewGuid().ToString("N");
        var staging = root + ".wsgm-stage-" + id;
        var backup = root + ".wsgm-backup-" + id;
        var marker = root + ".wsgm-update.json";
        try
        {
            Recover(root);
            Directory.CreateDirectory(staging);
            using (var archive = new ZipArchive(package, ZipArchiveMode.Read, true))
            {
                var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/').Split('/')[0])
                    .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count == 0 || names.Any(name => !SafeName(name)))
                {
                    throw new InvalidDataException("The theme package has an invalid root entry.");
                }

                foreach (var name in names)
                {
                    var old = Path.Combine(root, name);
                    if (Directory.Exists(old))
                    {
                        CopyDirectory(old, Path.Combine(staging, name));
                    }
                    else if (File.Exists(old))
                    {
                        File.Copy(old, Path.Combine(staging, name));
                    }
                }

                archive.ExtractToDirectory(staging, true);
            }

            foreach (var folder in Directory.EnumerateDirectories(staging))
            {
                var manifest = Path.Combine(folder, "theme.json");
                if (File.Exists(manifest))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                    _ = ThemeManifest.Parse(document.RootElement);
                }
                else if (!File.Exists(Path.Combine(folder, "theme.css")))
                {
                    throw new InvalidDataException("A theme folder must contain theme.json or theme.css.");
                }
            }

            var journal = new ThemeUpdateJournal
            {
                Id = id,
                Names = Directory.EnumerateFileSystemEntries(staging).Select(Path.GetFileName).Cast<string>().ToList()
            };
            journal.Existing = journal.Names.Where(name => Exists(Path.Combine(root, name))).ToList();
            AtomicFile.WriteText(marker, JsonSerializer.Serialize(journal), true);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(backup);
            foreach (var name in journal.Names)
            {
                var target = Path.Combine(root, name);
                if (Exists(target))
                {
                    Move(target, Path.Combine(backup, name));
                }

                Move(Path.Combine(staging, name), target);
            }

            journal.Committed = true;
            AtomicFile.WriteText(marker, JsonSerializer.Serialize(journal), true);
            Recover(root);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or NotSupportedException or JsonException or ThemeManifestException)
        {
            if (File.Exists(marker))
            {
                Recover(root);
            }

            throw new ThemeStoreException($"The theme package could not be unpacked: {ex.Message}");
        }
        finally
        {
            if (!File.Exists(marker) && Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    /// <summary>Completes or restores an interrupted theme-folder promotion before loading themes.</summary>
    internal static void Recover(string root)
    {
        root = Path.GetFullPath(root);
        var marker = root + ".wsgm-update.json";
        if (!File.Exists(marker))
        {
            return;
        }

        ThemeUpdateJournal journal;
        using (var stream = File.OpenRead(marker))
        {
            if (stream.Length > 128 * 1024)
            {
                throw new InvalidDataException("The theme update journal is too large.");
            }

            journal = JsonSerializer.Deserialize<ThemeUpdateJournal>(stream)
                      ?? throw new InvalidDataException("The theme update journal is empty.");
        }

        if (!Guid.TryParseExact(journal.Id, "N", out _) || journal.Names is null || journal.Existing is null
            || journal.Names.Count > 256
            || journal.Names.Any(name => !SafeName(name)) ||
            journal.Existing.Any(name => !journal.Names.Contains(name)))
        {
            throw new InvalidDataException("The theme update journal is invalid.");
        }

        var staging = root + ".wsgm-stage-" + journal.Id;
        var backup = root + ".wsgm-backup-" + journal.Id;
        if (!journal.Committed)
        {
            foreach (var name in journal.Names)
            {
                var target = Path.Combine(root, name);
                var previous = Path.Combine(backup, name);
                if (Exists(previous))
                {
                    Delete(target);
                    Move(previous, target);
                }
                else if (!journal.Existing.Contains(name) && !Exists(Path.Combine(staging, name)))
                {
                    Delete(target);
                }
            }

            journal.Committed = true; // Restoration is complete; only private staging cleanup remains.
            AtomicFile.WriteText(marker, JsonSerializer.Serialize(journal), true);
        }

        Delete(staging);
        Delete(backup);
        File.Delete(marker);
    }

    private static bool SafeName(string name)
    {
        return !string.IsNullOrEmpty(name) && name is not ("." or "..") && Path.GetFileName(name) == name
               && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool Exists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private static void Move(string source, string target)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A theme update cannot follow a reparse point.");
        }

        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if (Directory.Exists(entry))
            {
                CopyDirectory(entry, Path.Combine(target, Path.GetFileName(entry)));
            }
            else
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("A theme update cannot follow a reparse point.");
                }

                File.Copy(entry, Path.Combine(target, Path.GetFileName(entry)));
            }
        }
    }

    private sealed class ThemeUpdateJournal
    {
        public string Id { get; set; } = string.Empty;
        public List<string> Names { get; set; } = [];
        public List<string> Existing { get; set; } = [];
        public bool Committed { get; set; }
    }
}
