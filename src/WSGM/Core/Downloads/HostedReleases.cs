using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds the newest GitHub or Forgejo release that publishes exactly one matching asset.</summary>
/// <remarks>
///     Every URL is HTTPS through <see cref="DownloadClient" />. Drafts are skipped, as are prereleases on the
///     <c>stable</c> channel. More than one matching asset in a release is an error rather than a guess. GitHub's
///     <c>sha256:</c> asset digest, or a published checksum file, becomes <see cref="ReleaseAsset.Sha256" />.
/// </remarks>
internal sealed class HostedReleases(DownloadClient client)
{
    /// <summary>The supported hosted providers; this is the one list every catalog validates against.</summary>
    internal static readonly IReadOnlySet<string> Providers =
        new HashSet<string>(["GitHub", "Forgejo"], StringComparer.Ordinal);

    /// <summary>Finds the newest matching release.</summary>
    /// <param name="source">The repository and asset selection.</param>
    /// <param name="architecture">The architecture substituted into the asset pattern.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The release.</returns>
    /// <exception cref="InvalidDataException">
    ///     The provider is unsupported, no release has a unique matching asset, or its checksum file is unusable.
    /// </exception>
    internal async Task<HostedRelease> FindAsync(HostedReleaseSource source, string architecture,
        CancellationToken cancellationToken)
    {
        if (!Providers.Contains(source.Provider))
        {
            throw new InvalidDataException("Unsupported hosted release provider: " + source.Provider);
        }

        var baseUri = DownloadClient.DirectoryUri(source.BaseUrl);
        var repository = string.Join('/',
            source.Repositories.GetValueOrDefault(architecture, source.Repository).Split('/')
                .Select(Uri.EscapeDataString));
        var prefix = source.Provider == "GitHub" ? "repos/" : "api/v1/repos/";
        var pattern = source.AssetPattern.Replace("{arch}",
            source.AssetArchitectureNames.GetValueOrDefault(architecture, architecture),
            StringComparison.Ordinal).Replace("{variant}", source.AssetVariants.GetValueOrDefault(architecture, ""),
            StringComparison.Ordinal);
        if (pattern.Length == 0)
        {
            throw new InvalidDataException("Hosted release definitions require an explicit asset-selection pattern.");
        }

        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        for (var page = 1; ; page++)
        {
            var endpoint = prefix + repository + $"/releases?page={page}&per_page=100&limit=100";
            using var document = JsonDocument.Parse(await client.TextAsync(new Uri(baseUri, endpoint).AbsoluteUri,
                cancellationToken).ConfigureAwait(false));
            var releases = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            foreach (var release in releases)
            {
                if (JsonRead.Flag(release, "draft")
                    || source.Channel == "stable" && JsonRead.Flag(release, "prerelease"))
                {
                    continue;
                }

                var offered = Assets(release).ToList();
                // Definitions explicitly opt into release-body download links.
                if (offered.Count == 0 && source.BodyAssets)
                {
                    foreach (Match match in Regex.Matches(JsonRead.StringOrNumber(release, "body"),
                                 @"https://[^\s)\]<>""]+[.]zip", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    {
                        var uri = DownloadClient.Https(match.Value);
                        if (regex.IsMatch(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath))))
                        {
                            offered.Add(new ReleaseAsset(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)),
                                uri.AbsoluteUri));
                        }
                    }
                }

                var selected = offered.Where(asset => regex.IsMatch(asset.Name)).DistinctBy(asset => asset.Url)
                    .ToArray();
                if (selected.Length > 1)
                {
                    throw new InvalidDataException(
                        "Multiple release assets match this architecture/channel definition.");
                }

                if (selected.Length == 1)
                {
                    var tag = JsonRead.StringOrNumber(release, "tag_name");
                    var asset = selected[0];
                    if (source.ChecksumFile.Length > 0)
                    {
                        var checksumName = source.ChecksumFile.Replace("{asset}", asset.Name, StringComparison.Ordinal);
                        var checksum = offered.SingleOrDefault(item => item.Name == checksumName);
                        if (checksum is not null)
                        {
                            var text = await client.TextAsync(checksum.Url, cancellationToken).ConfigureAwait(false);
                            var sha = ParseChecksum(text, asset.Name,
                                checksum.Name.StartsWith(asset.Name, StringComparison.OrdinalIgnoreCase));
                            asset = asset with { Sha256 = sha };
                        }
                    }

                    return new HostedRelease(tag.TrimStart('v'),
                        tag + ":" + JsonRead.StringOrNumber(release, "id") + ":" + asset.Sha256
                        + (asset.Revision.Length > 0 ? ":" + asset.Revision : ""),
                        JsonRead.StringOrNumber(release, "html_url"), source.Provider, asset);
                }
            }

            if (releases.Length == 0)
            {
                break;
            }
        }

        throw new InvalidDataException("No unique matching Windows release asset was published for this channel.");
    }

    private static IEnumerable<ReleaseAsset> Assets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var digest = JsonRead.StringOrNumber(asset, "digest");
            yield return new ReleaseAsset(JsonRead.StringOrNumber(asset, "name"),
                JsonRead.StringOrNumber(asset, "browser_download_url"),
                digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : "",
                asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0,
                JsonRead.StringOrNumber(asset, "id") + ":" + JsonRead.StringOrNumber(asset, "updated_at"));
        }
    }

    private static string ParseChecksum(string text, string assetName, bool specificFile)
    {
        if (specificFile && UpdateChecker.ParseHash(text) is { } direct)
        {
            return direct;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line.Trim(), "^([a-fA-F0-9]{64})(?:\\s+\\*?(.+))?$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success && (match.Groups[2].Value.Trim().Equals(assetName, StringComparison.Ordinal)
                                  || (specificFile && match.Groups[2].Value.Length == 0)))
            {
                return match.Groups[1].Value.ToLowerInvariant();
            }

            match = Regex.Match(line.Trim(), "^SHA256\\s*\\((.+)\\)\\s*=\\s*([a-fA-F0-9]{64})$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success && match.Groups[1].Value.Equals(assetName, StringComparison.Ordinal))
            {
                return match.Groups[2].Value.ToLowerInvariant();
            }
        }

        throw new InvalidDataException(
            "The upstream checksum file does not contain a matching SHA-256 for this package.");
    }
}
