using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Discovers versioned portable archives on the emulator publisher's download page.</summary>
internal static class EmulatorWebReleases
{
    internal static async Task<EmulatorRelease> FindAsync(DownloadClient client, EmulatorSource source,
        string architecture, CancellationToken token)
    {
        var page = DownloadClient.Https(source.BaseUrl);
        var text = await client.TextAsync(page.AbsoluteUri, token).ConfigureAwait(false);
        var pattern = source.AssetPattern.Replace("{arch}",
            source.AssetArchitectureNames.GetValueOrDefault(architecture, architecture), StringComparison.Ordinal);
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        // EmuDeck's legacy Model 2 distribution is documented in a PowerShell definition.
        // Read its quoted archive URL as data; never evaluate or run downloaded scripts.
        var matches = Regex.Matches(text, "(?:href=|(?=[\"']https://))[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1))
            .Select(match => new Uri(page, WebUtility.HtmlDecode(match.Groups[1].Value)))
            .Where(uri => uri.Scheme == Uri.UriSchemeHttps)
            .Select(uri => (Uri: uri, Name: Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath))))
            .Where(asset => regex.IsMatch(asset.Name)).DistinctBy(asset => asset.Uri.AbsoluteUri).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The publisher did not list one matching Windows portable archive.");
        }

        var asset = matches[0];
        var version = regex.Match(asset.Name).Groups["version"].Value;
        if (version.Length == 0)
        {
            throw new InvalidDataException("The portable download has no versioned release identity.");
        }

        var hash = "";
        if (source.ChecksumFile.StartsWith("sha256:", StringComparison.Ordinal))
        {
            hash = source.ChecksumFile[7..];
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                throw new InvalidDataException("The bundled legacy distribution checksum is invalid.");
            }
        }
        else if (source.ChecksumFile == "{asset}.sha256")
        {
            var checksum = await client.TextAsync(asset.Uri.AbsoluteUri + ".sha256", token).ConfigureAwait(false);
            hash = UpdateChecker.ParseHash(checksum)
                   ?? throw new InvalidDataException("The publisher's checksum file contains no SHA-256.");
        }

        return new EmulatorRelease(version, asset.Uri.AbsoluteUri + ":" + hash, page.AbsoluteUri, "WebPage",
            new ReleaseAsset(asset.Name, asset.Uri.AbsoluteUri, hash));
    }
}
