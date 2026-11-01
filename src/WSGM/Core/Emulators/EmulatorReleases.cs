using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Release discovery has no installer scripts, shims or emulator-specific launch policy.</summary>
internal sealed class EmulatorReleases(DownloadClient client)
{
    private readonly HostedReleases _hosted = new(client);

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

        if (HostedReleases.Providers.Contains(source.Provider))
        {
            var hosted = await _hosted.FindAsync(Hosted(source, channel), architecture, cancellationToken)
                .ConfigureAwait(false);
            return new EmulatorRelease(hosted.Version, hosted.Id, hosted.NotesUrl, hosted.Provider, hosted.Asset);
        }

        return source.Provider switch
        {
            "Scoop" => await ScoopAsync(source, architecture, cancellationToken).ConfigureAwait(false),
            "Buildbot" => await BuildbotAsync(source, architecture, cancellationToken).ConfigureAwait(false),
            "Dolphin" => await DolphinAsync(source, architecture, cancellationToken).ConfigureAwait(false),
            "WebPage" => await EmulatorWebReleases.FindAsync(client, source, architecture, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidDataException("Unsupported emulator release provider: " + source.Provider)
        };
    }

    private static HostedReleaseSource Hosted(EmulatorSource source, string channel)
    {
        return new HostedReleaseSource
        {
            Provider = source.Provider,
            BaseUrl = source.BaseUrl,
            Repository = source.Repository,
            Repositories = source.Repositories,
            AssetPattern = source.AssetPattern,
            AssetArchitectureNames = source.AssetArchitectureNames,
            AssetVariants = source.AssetVariants,
            Channel = channel,
            BodyAssets = source.BodyAssets,
            ChecksumFile = source.ChecksumFile
        };
    }

    private async Task<EmulatorRelease> ScoopAsync(EmulatorSource source, string architecture,
        CancellationToken cancellationToken)
    {
        var manifestUrl = new Uri(DownloadClient.DirectoryUri(source.BaseUrl), source.Manifest).AbsoluteUri;
        using var document = JsonDocument.Parse(await client.TextAsync(manifestUrl, cancellationToken)
            .ConfigureAwait(false));
        var root = document.RootElement;
        var version = JsonRead.StringOrNumber(root, "version");
        var package = root;
        if (root.TryGetProperty("architecture", out var architectures))
        {
            if (!architectures.TryGetProperty(architecture == "x64" ? "64bit" : "arm64", out package))
            {
                throw new InvalidDataException("The Scoop manifest has no matching architecture.");
            }
        }

        var url = JsonRead.StringOrNumber(package, "url");
        var hash = JsonRead.StringOrNumber(package, "hash");
        if (url.Length == 0)
        {
            url = JsonRead.StringOrNumber(root, "url");
        }

        if (hash.Length == 0)
        {
            hash = JsonRead.StringOrNumber(root, "hash");
        }

        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
        {
            throw new InvalidDataException("A matching SHA-256 is required for the Scoop package.");
        }

        var uri = DownloadClient.Https(url);
        var name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        var notes = JsonRead.StringOrNumber(root, "homepage");
        var extract = JsonRead.StringOrNumber(package, "extract_dir");
        if (extract.Length == 0)
        {
            extract = JsonRead.StringOrNumber(root, "extract_dir");
        }

        return new EmulatorRelease(version, version + ":" + hash.ToLowerInvariant(), notes, "Scoop",
            new ReleaseAsset(name, url, hash), extract);
    }

    private async Task<EmulatorRelease> BuildbotAsync(EmulatorSource source, string architecture,
        CancellationToken cancellationToken)
    {
        var root = DownloadClient.DirectoryUri(source.BaseUrl);
        if (!source.PlatformNames.TryGetValue(architecture, out var platform))
        {
            throw new InvalidOperationException("The supported Windows Buildbot catalogue is x64.");
        }

        string version;
        string packageUrl;
        if (source.Repository == "stable")
        {
            var text = await client.TextAsync(new Uri(root, "stable/").AbsoluteUri, cancellationToken)
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
            var text = await client.TextAsync(new Uri(root, $"nightly/windows/{platform}/").AbsoluteUri,
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
            new ReleaseAsset(source.PackageName, packageUrl), CoreIndexUrl: coreRoot,
            CoreAssetsUrl: new Uri(root, source.AssetPath).AbsoluteUri);
    }

    private async Task<EmulatorRelease> DolphinAsync(EmulatorSource source, string architecture,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await client.TextAsync(new Uri(
            DownloadClient.DirectoryUri(source.BaseUrl),
            "update/latest/" + source.Repository).AbsoluteUri, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var system = architecture == "x64" ? "Windows x64" : "Windows arm64";
        var artifacts = root.GetProperty("artifacts").EnumerateArray()
            .Where(asset => JsonRead.StringOrNumber(asset, "system") == system).ToArray();
        if (artifacts.Length != 1)
        {
            throw new InvalidDataException("Dolphin did not publish one matching Windows package.");
        }

        var url = JsonRead.StringOrNumber(artifacts[0], "url");
        var version = JsonRead.StringOrNumber(root, "shortrev");
        if (version.Length == 0)
        {
            version = JsonRead.StringOrNumber(root, "version");
        }

        return new EmulatorRelease(version, source.Repository + ":" + JsonRead.StringOrNumber(root, "hash"),
            new Uri(DownloadClient.Https(source.BaseUrl), "download/").AbsoluteUri, "Dolphin",
            new ReleaseAsset(Path.GetFileName(DownloadClient.Https(url).AbsolutePath), url));
    }
}
