using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

internal sealed partial class GameToolReleases
{
    private async Task<HostedRelease> WebsiteReleaseAsync(GameToolSource source, CancellationToken token)
    {
        var page = await _client.TextAsync(source.DownloadPage, token).ConfigureAwait(false);
        return WebsiteRelease(source, page);
    }

    /// <summary>Selects the newest matching official asset from a download page.</summary>
    /// <param name="source">The website descriptor, including asset pattern and download root.</param>
    /// <param name="page">The downloaded page.</param>
    /// <returns>The newest release with a numeric version capture.</returns>
    internal static HostedRelease WebsiteRelease(GameToolSource source, string page)
    {
        var candidates = Regex.Matches(page, source.AssetPattern, RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1))
            .Select(match => (Name: match.Value, Version: match.Groups["version"].Value))
            .Distinct().Where(item => Version.TryParse(item.Version, out _))
            .OrderByDescending(item => Version.Parse(item.Version)).ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidDataException("The official download page has no matching versioned asset.");
        }

        var newest = candidates[0];
        if (!DownloadClient.IsSafeAssetName(newest.Name))
        {
            throw new InvalidDataException("The official asset has an unsafe name.");
        }

        return new HostedRelease(newest.Version, newest.Version, source.DownloadPage, source.Provider,
            new ReleaseAsset(newest.Name, new Uri(new Uri(source.DownloadBaseUrl), newest.Name).AbsoluteUri));
    }

    private static void StagePayload(GameToolMode mode, string extracted, string destination, string architecture,
        CancellationToken token)
    {
        if (mode.Files.Count == 0)
        {
            Directory.Move(extracted, destination);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var (pattern, relative) in mode.Files)
        {
            token.ThrowIfCancellationRequested();
            var folder = mode.ArchitectureFolders.GetValueOrDefault(architecture) ?? architecture;
            var source = Path.GetFullPath(Path.Combine(extracted, pattern.Replace("{arch}", folder,
                StringComparison.Ordinal).Replace("{bits}", architecture == "x64" ? "64" : "32", StringComparison.Ordinal)));
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!StoragePaths.IsUnder(extracted, source) || !StoragePaths.IsUnder(destination, target)
                || !File.Exists(source))
            {
                throw new InvalidDataException("The downloaded package is missing " + pattern + ".");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private static GameToolPackage ValidatePrepared(GameToolDefinition tool, GameToolMode mode, HostedRelease release,
        string root, string architecture)
    {
        foreach (var file in mode.Files.Values)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path) || (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                      && PeImage.TryArchitecture(path) != architecture))
            {
                throw new InvalidDataException("The downloaded " + file + " is missing or has the wrong architecture.");
            }
        }

        var validated = Validate(tool, release, root);
        if (validated.Architecture != architecture)
        {
            throw new InvalidDataException("The downloaded tool does not match the game's architecture.");
        }

        return mode.Files.Count == 0 ? validated : validated with { Version = release.Version };
    }
}
