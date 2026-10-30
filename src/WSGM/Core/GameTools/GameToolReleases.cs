using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core.GameTools;

/// <summary>A verified, extracted tool package ready to be copied into a game.</summary>
/// <param name="Release">The release it came from.</param>
/// <param name="Root">The payload root, after unwrapping a single top-level folder.</param>
/// <param name="Version">The primary file's product version.</param>
/// <param name="Family">The primary file's version family.</param>
/// <param name="Architecture">The primary file's PE architecture.</param>
internal sealed record GameToolPackage(EmulatorRelease Release, string Root, string Version, string Family,
    string Architecture);

/// <summary>The cached result of the latest-release lookup.</summary>
/// <param name="Release">The release, or null when the lookup failed.</param>
/// <param name="Error">Why the lookup failed.</param>
/// <param name="CheckedUtc">When the lookup ran.</param>
internal sealed record GameToolLatest(EmulatorRelease? Release, string? Error, DateTime CheckedUtc);

/// <summary>Finds, downloads, verifies and caches tool packages through the shared release adapters.</summary>
/// <remarks>
///     Latest-release lookups are cached in memory for the session, because GitHub's anonymous API limit is
///     small; an explicit "Check for updates" refreshes. Downloads fail closed: a release without a
///     published SHA-256 is refused. Packages are kept extracted under
///     <c>&lt;root&gt;\GameTools\packages\&lt;tool&gt;\&lt;release&gt;</c>, keyed by version, asset, hash and URL.
/// </remarks>
internal sealed class GameToolReleases : IDisposable
{
    private static readonly TimeSpan LatestLifetime = TimeSpan.FromHours(6);

    private readonly string _userRoot;
    private readonly EmulatorNetwork _network;
    private readonly EmulatorReleases _releases;
    private readonly Dictionary<string, GameToolLatest> _latest = new(StringComparer.Ordinal);
    private readonly object _latestGate = new();
    private int _swept;

    /// <summary>Creates the release owner.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="handler">An optional HTTP handler, the test seam.</param>
    internal GameToolReleases(string userRoot, HttpMessageHandler? handler)
    {
        _userRoot = userRoot;
        _network = new EmulatorNetwork(handler);
        _releases = new EmulatorReleases(_network);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _network.Dispose();
    }

    /// <summary>The cached latest release without contacting the network.</summary>
    /// <param name="tool">The tool.</param>
    /// <returns>The cached answer, or null when none has been looked up.</returns>
    internal GameToolLatest? Cached(GameToolDefinition tool)
    {
        lock (_latestGate)
        {
            return _latest.GetValueOrDefault(tool.Id);
        }
    }

    /// <summary>Looks up the latest release, reusing a recent answer unless forced.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="force">Whether to bypass the cache.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The answer; a failure is carried in it rather than thrown.</returns>
    internal async Task<GameToolLatest> LatestAsync(GameToolDefinition tool, bool force,
        CancellationToken cancellationToken)
    {
        if (!force && Cached(tool) is { } cached && DateTime.UtcNow - cached.CheckedUtc < LatestLifetime
            && cached.Release is not null)
        {
            return cached;
        }

        GameToolLatest answer;
        try
        {
            var release = await _releases.FindHostedAsync(tool.Source.Hosted(), tool.Source.Channel,
                tool.Architectures[0], cancellationToken).ConfigureAwait(false);
            answer = new GameToolLatest(release, null, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
                                       or TimeoutException or IOException or KeyNotFoundException)
        {
            Log.Warn($"Game Tools: release lookup for {tool.Id} failed: {ex.Message}");
            answer = new GameToolLatest(null, ReleaseError(ex), DateTime.UtcNow);
        }

        lock (_latestGate)
        {
            _latest[tool.Id] = answer;
        }

        return answer;
    }

    /// <summary>Returns a verified package for a release, downloading it once.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="release">The release.</param>
    /// <param name="progress">Receives the phase text, the bytes copied and the expected total.</param>
    /// <param name="cancellationToken">Cancels the download and extraction.</param>
    /// <returns>The package.</returns>
    internal async Task<GameToolPackage> PrepareAsync(GameToolDefinition tool, EmulatorRelease release,
        Action<string, long, long> progress, CancellationToken cancellationToken)
    {
        SweepStages(tool);
        var sha = NormalizeDigest(release.Asset.Sha256)
                  ?? throw new InvalidDataException(
                      "The publisher didn't provide a checksum for this download, so WSGM won't install it.");
        if (!Path.GetExtension(release.Asset.Name).TrimStart('.').Equals(tool.Source.PackageType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The published package is not the expected ." + tool.Source.PackageType
                                           + " archive.");
        }

        var identity = string.Join('|', release.Version, release.Asset.Name, sha, release.Asset.Url);
        var directory = PackageDirectory(tool, release.Version, sha);
        var payload = Path.Combine(directory, "payload");
        if (File.Exists(Path.Combine(directory, "identity.txt"))
            && File.ReadAllText(Path.Combine(directory, "identity.txt")) == identity && Directory.Exists(payload))
        {
            try
            {
                return Validate(tool, release, Unwrap(payload));
            }
            catch (InvalidDataException)
            {
                // A damaged cache is replaced by a fresh verified download below.
            }
        }

        var stage = directory + ".stage-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, release.Asset.Name);
            await DownloadAsync(release.Asset with { Sha256 = sha }, archive,
                copied => progress("Downloading " + tool.Name, copied, release.Asset.Size), cancellationToken)
                .ConfigureAwait(false);
            progress("Extracting", 0, 0);
            var extracted = Path.Combine(stage, "payload");
            await Task.Run(() => EmulatorPackages.Extract(archive, extracted, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            File.Delete(archive);
            _ = Validate(tool, release, Unwrap(extracted));
            File.WriteAllText(Path.Combine(stage, "identity.txt"), identity);

            // Promote: move the old entry aside, move the stage in, then delete the old entry.
            var old = directory + ".old-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(directory))
            {
                Directory.Move(directory, old);
            }

            try
            {
                Directory.Move(stage, directory);
            }
            catch
            {
                if (Directory.Exists(old))
                {
                    Directory.Move(old, directory);
                }

                throw;
            }

            DeleteTree(old);
            return Validate(tool, release, Unwrap(payload));
        }
        finally
        {
            DeleteTree(stage);
        }
    }

    /// <summary>Normalizes a published digest to 64 lowercase hex characters.</summary>
    /// <param name="digest">The digest, optionally prefixed with <c>sha256:</c>.</param>
    /// <returns>The normalized hash, or null when it is missing or malformed.</returns>
    internal static string? NormalizeDigest(string? digest)
    {
        var value = (digest ?? "").Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        return Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? value.ToLowerInvariant()
            : null;
    }

    /// <summary>Validates an extracted payload against the definition.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="release">The release.</param>
    /// <param name="root">The payload root.</param>
    /// <returns>The package.</returns>
    /// <exception cref="InvalidDataException">The payload is incomplete or not the expected tool.</exception>
    internal static GameToolPackage Validate(GameToolDefinition tool, EmulatorRelease release, string root)
    {
        foreach (var required in tool.Payload.Required)
        {
            var path = Path.Combine(root, required);
            if (!File.Exists(path))
            {
                throw new InvalidDataException("The downloaded package is missing " + required + ".");
            }
        }

        var primary = Path.Combine(root, tool.PrimaryFile);
        if (!File.Exists(primary) || new FileInfo(primary).Length == 0)
        {
            throw new InvalidDataException("The downloaded package has no usable " + tool.PrimaryFile + ".");
        }

        var info = PeImage.TryVersion(primary);
        if (info is null || !GameToolIdentityCheck.IsTool(tool, info))
        {
            throw new InvalidDataException("The downloaded " + tool.PrimaryFile + " is not " + tool.Name + ".");
        }

        var architecture = PeImage.TryArchitecture(primary) ?? "";
        if (!tool.Architectures.Contains(architecture, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The downloaded " + tool.Name + " is not a supported architecture.");
        }

        var family = GameToolVersions.Family(tool, info);
        if (tool.Payload.FamilyDirectories.TryGetValue(family, out var directories)
            && directories.Any(directory => !Directory.Exists(Path.Combine(root, directory))))
        {
            throw new InvalidDataException("The downloaded package is incomplete for " + tool.Name + " "
                                           + GameToolVersions.NumericToken(info.ProductVersion) + ".");
        }

        var version = info.ProductVersion.Length > 0 ? info.ProductVersion : info.FileVersion;
        return new GameToolPackage(release, root, version.Length > 0 ? version : release.Version, family,
            architecture);
    }

    /// <summary>Builds a release record from a manifest so a repair can fetch the installed version again.</summary>
    /// <param name="manifest">The installed manifest.</param>
    /// <returns>The release.</returns>
    internal static EmulatorRelease FromManifest(GameToolManifest manifest)
    {
        return new EmulatorRelease(manifest.ReleaseVersion, manifest.ReleaseId, "", "",
            new EmulatorAsset(manifest.AssetName, manifest.SourceUrl, manifest.AssetSha256));
    }

    private async Task DownloadAsync(EmulatorAsset asset, string destination, Action<long> copied,
        CancellationToken cancellationToken)
    {
        // Three transfer attempts on transient failures, and a single verified re-download after a
        // hash mismatch. Both are bounded re-fetches of a verified file, not repeated writes to a game.
        var mismatches = 0;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                if (asset.Name.Length == 0 || Path.GetFileName(asset.Name) != asset.Name
                                           || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    throw new IOException("The published package has an unsafe file name.");
                }

                await VerifiedDownload.WriteAsync(token => _network.OpenAsync(asset.Url, token), destination,
                    asset.Sha256, asset.Size, cancellationToken, copied).ConfigureAwait(false);
                return;
            }
            catch (InvalidDataException) when (mismatches++ == 0)
            {
                Log.Warn("Game Tools: download failed verification; fetching it once more.");
            }
            catch (Exception ex) when (attempt < 3 && Transient(ex) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }

            copied(0);
        }
    }

    private static bool Transient(Exception ex)
    {
        return ex switch
        {
            HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests || (int)status >= 500,
            HttpRequestException => true,
            TimeoutException or IOException => true,
            _ => false
        };
    }

    private static string ReleaseError(Exception ex)
    {
        return ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
            ? "GitHub is limiting requests right now. Try again later."
            : ex is HttpRequestException or TimeoutException
                ? "Couldn't reach GitHub to check for releases."
                : "Couldn't read the published releases: " + ex.Message;
    }

    private string PackageDirectory(GameToolDefinition tool, string version, string sha)
    {
        var safe = new string(version.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-'
            ? character
            : '_').ToArray());
        return Path.Combine(GameToolStorage.DataRoot(_userRoot), "packages", tool.Id, safe + "-" + sha[..12]);
    }

    private static string Unwrap(string root)
    {
        var entries = Directory.GetFileSystemEntries(root);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : root;
    }

    private void SweepStages(GameToolDefinition tool)
    {
        if (Interlocked.Exchange(ref _swept, 1) != 0)
        {
            return;
        }

        var root = Path.Combine(GameToolStorage.DataRoot(_userRoot), "packages", tool.Id);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var stale in Directory.EnumerateDirectories(root)
                     .Where(path => path.Contains(".stage-", StringComparison.Ordinal)
                                    || path.Contains(".old-", StringComparison.Ordinal)))
        {
            DeleteTree(stale);
        }
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Game Tools: could not remove staging folder {path}: {ex.Message}");
        }
    }
}

/// <summary>Decides whether a binary is a tool's own build, regardless of its file name.</summary>
internal static class GameToolIdentityCheck
{
    /// <summary>Whether a version resource identifies the tool.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="info">The version resource, already trimmed.</param>
    /// <returns>
    ///     True when OriginalFilename matches ordinally ignoring case and no veto token (such as ReShade)
    ///     appears in any of the seven identity fields. The veto is checked first.
    /// </returns>
    internal static bool IsTool(GameToolDefinition tool, PeVersionInfo info)
    {
        if (tool.Identity.VetoTokens.Any(info.Mentions))
        {
            return false;
        }

        return info.OriginalFilename.Equals(tool.Identity.OriginalFilename, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Names another known mod a file belongs to.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="info">The version resource.</param>
    /// <returns>The other product's name, or null.</returns>
    internal static string? OtherTool(GameToolDefinition tool, PeVersionInfo info)
    {
        return tool.Identity.OtherTools.FirstOrDefault(other => info.Mentions(other.Token))?.Name;
    }
}
