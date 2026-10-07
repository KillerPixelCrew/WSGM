using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

internal sealed record EmulatorAsset(string Name, string Url, string Sha256 = "", long Size = 0);

internal sealed record EmulatorRelease(
    string Version,
    string Id,
    string NotesUrl,
    string Provider,
    EmulatorAsset Asset,
    string ExtractDirectory = "",
    string CoreIndexUrl = "",
    string CoreAssetsUrl = "");

/// <summary>Release metadata and package transport shared by the reviewed source adapters.</summary>
internal sealed class EmulatorNetwork : IDisposable
{
    private readonly HttpClient _http;

    public EmulatorNetwork(HttpMessageHandler? handler)
    {
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, false);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WSGM-EmulatorManager/2.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public static Uri Https(string value)
    {
        if (JsonRead.HttpsUrl(value).Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("Emulator sources must use HTTPS.");
        }

        return uri;
    }

    public static Uri DirectoryUri(string value)
    {
        var uri = Https(value);
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    public async Task<HttpResponseMessage> OpenAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync(Https(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(url, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Release discovery has no installer scripts, shims or emulator-specific launch policy.</summary>
internal sealed class EmulatorReleases(EmulatorNetwork network)
{
    public async Task<EmulatorRelease> FindAsync(EmulatorPackageDefinition definition, string channel,
        string architecture, CancellationToken cancellationToken)
    {
        if (!definition.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(definition.Name + " has no supported " + architecture + " package.");
        }

        if (!definition.Sources.TryGetValue(channel, out var source))
        {
            throw new InvalidOperationException("Choose a supported release channel for " + definition.Name + ".");
        }

        return source.Provider switch
        {
            "Scoop" => await ScoopAsync(source, architecture, cancellationToken).ConfigureAwait(false),
            "GitHub" => await HostedAsync(source, channel, architecture, true, cancellationToken).ConfigureAwait(false),
            "Forgejo" => await HostedAsync(source, channel, architecture, false, cancellationToken)
                .ConfigureAwait(false),
            "Buildbot" => await BuildbotAsync(source, architecture, cancellationToken).ConfigureAwait(false),
            "Dolphin" => await DolphinAsync(source, architecture, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException("Unsupported emulator release provider: " + source.Provider)
        };
    }

    private async Task<EmulatorRelease> ScoopAsync(EmulatorSource source, string architecture,
        CancellationToken cancellationToken)
    {
        var manifestUrl = new Uri(EmulatorNetwork.DirectoryUri(source.BaseUrl), source.Manifest).AbsoluteUri;
        using var document = JsonDocument.Parse(await network.TextAsync(manifestUrl, cancellationToken)
            .ConfigureAwait(false));
        var root = document.RootElement;
        var version = Text(root, "version");
        var package = root;
        if (root.TryGetProperty("architecture", out var architectures))
        {
            if (!architectures.TryGetProperty(architecture == "x64" ? "64bit" : "arm64", out package))
            {
                throw new InvalidDataException("The Scoop manifest has no matching architecture.");
            }
        }

        var url = Text(package, "url");
        var hash = Text(package, "hash");
        if (url.Length == 0)
        {
            url = Text(root, "url");
        }

        if (hash.Length == 0)
        {
            hash = Text(root, "hash");
        }

        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
        {
            throw new InvalidDataException("A matching SHA-256 is required for the Scoop package.");
        }

        var uri = EmulatorNetwork.Https(url);
        var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        var notes = Text(root, "homepage");
        var extract = Text(package, "extract_dir");
        if (extract.Length == 0)
        {
            extract = Text(root, "extract_dir");
        }

        return new EmulatorRelease(version, version + ":" + hash.ToLowerInvariant(), notes, "Scoop",
            new EmulatorAsset(name, url, hash), extract);
    }

    private async Task<EmulatorRelease> HostedAsync(EmulatorSource source, string channel, string architecture,
        bool github, CancellationToken cancellationToken)
    {
        var baseUri = EmulatorNetwork.DirectoryUri(source.BaseUrl);
        var repository = string.Join('/',
            source.Repositories.GetValueOrDefault(architecture, source.Repository).Split('/')
                .Select(Uri.EscapeDataString));
        var prefix = github ? "repos/" : "api/v1/repos/";
        var pattern = source.AssetPattern.Replace("{arch}",
            source.AssetArchitectureNames.GetValueOrDefault(architecture, architecture),
            StringComparison.Ordinal).Replace("{variant}", source.AssetVariants.GetValueOrDefault(architecture, ""),
            StringComparison.Ordinal);
        if (pattern.Length == 0)
        {
            throw new InvalidDataException("Hosted release definitions require an explicit asset-selection pattern.");
        }

        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        for (var page = 1;; page++)
        {
            var endpoint = prefix + repository + $"/releases?page={page}&per_page=100&limit=100";
            using var document = JsonDocument.Parse(await network.TextAsync(new Uri(baseUri, endpoint).AbsoluteUri,
                cancellationToken).ConfigureAwait(false));
            var releases = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            foreach (var release in releases)
            {
                if (JsonRead.Flag(release, "draft"))
                {
                    continue;
                }

                var offered = Assets(release).ToList();
                // Definitions explicitly opt into release-body download links.
                if (offered.Count == 0 && source.BodyAssets)
                {
                    foreach (Match match in Regex.Matches(Text(release, "body"), @"https://[^\s)\]<>""]+[.]zip",
                                 RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    {
                        var uri = EmulatorNetwork.Https(match.Value);
                        if (regex.IsMatch(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath))))
                        {
                            offered.Add(new EmulatorAsset(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)),
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
                    var tag = Text(release, "tag_name");
                    var asset = selected[0];
                    if (source.ChecksumFile.Length > 0)
                    {
                        var checksumName = source.ChecksumFile.Replace("{asset}", asset.Name, StringComparison.Ordinal);
                        var checksum = offered.SingleOrDefault(item => item.Name == checksumName);
                        if (checksum is not null)
                        {
                            var text = await network.TextAsync(checksum.Url, cancellationToken).ConfigureAwait(false);
                            var sha = ParseChecksum(text, asset.Name,
                                checksum.Name.StartsWith(asset.Name, StringComparison.OrdinalIgnoreCase));
                            asset = asset with { Sha256 = sha };
                        }
                    }

                    return new EmulatorRelease(tag.TrimStart('v'), tag + ":" + Text(release, "id") + ":" + asset.Sha256,
                        Text(release, "html_url"), source.Provider, asset);
                }
            }

            if (releases.Length == 0)
            {
                break;
            }
        }

        throw new InvalidDataException("No unique matching Windows release asset was published for this channel.");
    }

    private async Task<EmulatorRelease> BuildbotAsync(EmulatorSource source, string architecture,
        CancellationToken cancellationToken)
    {
        var root = EmulatorNetwork.DirectoryUri(source.BaseUrl);
        if (!source.PlatformNames.TryGetValue(architecture, out var platform))
        {
            throw new InvalidOperationException("The supported Windows Buildbot catalogue is x64.");
        }

        string version;
        string packageUrl;
        if (source.Repository == "stable")
        {
            var text = await network.TextAsync(new Uri(root, "stable/").AbsoluteUri, cancellationToken)
                .ConfigureAwait(false);
            var versions = Regex.Matches(text, "href=\"(?:[^\"]*/)?([0-9]+[.][0-9]+[.][0-9]+)/\"",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value)
                .Distinct().Select(value => (Value: value, Version: Version.Parse(value)))
                .OrderByDescending(item => item.Version).ToArray();
            if (versions.Length == 0)
            {
                throw new InvalidDataException("Libretro did not publish a discoverable stable version.");
            }

            version = versions[0].Value;
            packageUrl = new Uri(root, $"stable/{version}/windows/{platform}/{source.PackageName}").AbsoluteUri;
        }
        else
        {
            var text = await network.TextAsync(new Uri(root, $"nightly/windows/{platform}/").AbsoluteUri,
                cancellationToken).ConfigureAwait(false);
            var names = Regex.Matches(text, "href=\"([^\"]+)\"", RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)).Select(match => match.Groups[1].Value)
                .Select(name => Path.GetFileName(name))
                .Where(name => Regex.IsMatch(name, source.PackagePattern, RegexOptions.CultureInvariant))
                .OrderByDescending(name => name, StringComparer.Ordinal).ToArray();
            if (names.Length == 0)
            {
                throw new InvalidDataException("Libretro did not publish an immutable date-named nightly frontend.");
            }

            version = names[0];
            packageUrl = new Uri(root, $"nightly/windows/{platform}/{names[0]}").AbsoluteUri;
        }

        var coreRoot = new Uri(root, source.CorePath.Replace("{platform}", platform, StringComparison.Ordinal))
            .AbsoluteUri;
        return new EmulatorRelease(version, source.Repository + ":" + version, root.AbsoluteUri, "Buildbot",
            new EmulatorAsset(source.PackageName, packageUrl), CoreIndexUrl: coreRoot,
            CoreAssetsUrl: new Uri(root, source.AssetPath).AbsoluteUri);
    }

    private async Task<EmulatorRelease> DolphinAsync(EmulatorSource source, string architecture,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await network.TextAsync(new Uri(
            EmulatorNetwork.DirectoryUri(source.BaseUrl),
            "update/latest/" + source.Repository).AbsoluteUri, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var system = architecture == "x64" ? "Windows x64" : "Windows arm64";
        var artifacts = root.GetProperty("artifacts").EnumerateArray()
            .Where(asset => Text(asset, "system") == system).ToArray();
        if (artifacts.Length != 1)
        {
            throw new InvalidDataException("Dolphin did not publish one matching Windows package.");
        }

        var url = Text(artifacts[0], "url");
        var version = Text(root, "shortrev");
        if (version.Length == 0)
        {
            version = Text(root, "version");
        }

        return new EmulatorRelease(version, source.Repository + ":" + Text(root, "hash"),
            new Uri(EmulatorNetwork.Https(source.BaseUrl), "download/").AbsoluteUri, "Dolphin",
            new EmulatorAsset(Path.GetFileName(EmulatorNetwork.Https(url).AbsolutePath), url));
    }

    private static IEnumerable<EmulatorAsset> Assets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var digest = Text(asset, "digest");
            yield return new EmulatorAsset(Text(asset, "name"), Text(asset, "browser_download_url"),
                digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : "",
                asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0);
        }
    }

    internal static string Text(JsonElement element, string property)
    {
        return JsonRead.OptionalString(element, property)
               ?? (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                   ? value.GetRawText()
                   : "");
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
