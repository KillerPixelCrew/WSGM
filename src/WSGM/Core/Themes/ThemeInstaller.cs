using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

        var package = await _client.DownloadBlobAsync(downloadId, cancellationToken).ConfigureAwait(false);
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
    /// <param name="package">The zip's bytes.</param>
    /// <param name="root">The themes folder.</param>
    /// <exception cref="ThemeStoreException">The zip could not be read or would write outside the folder.</exception>
    public static void Unpack(byte[] package, string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            using MemoryStream stream = new(package, false);
            ZipFile.ExtractToDirectory(stream, root, true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            throw new ThemeStoreException($"The theme package could not be unpacked: {ex.Message}");
        }
    }
}
