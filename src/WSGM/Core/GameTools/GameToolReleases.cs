using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Finds, downloads, verifies and caches tool packages through the shared release adapters.</summary>
/// <remarks>
///     Latest-release lookups are cached in memory for the session, because GitHub's anonymous API limit is
///     small: an answer for six hours and a failure for ten minutes. An explicit "Check for updates"
///     refreshes. Downloads fail closed: a release without a published SHA-256 is refused. Packages are kept
///     extracted under <c>&lt;root&gt;\GameTools\packages\&lt;tool&gt;\&lt;release&gt;</c>, keyed by version, asset,
///     hash and URL, with the hash of every payload file recorded beside them.
/// </remarks>
internal sealed class GameToolReleases : IDisposable
{
    private const string IdentityFile = "identity.txt";
    private const string HashesFile = "hashes.txt";

    private static readonly TimeSpan LatestLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(10);

    private readonly string _userRoot;
    private readonly DownloadClient _client;
    private readonly HostedReleases _releases;
    private readonly Dictionary<string, GameToolLatest> _latest = new(StringComparer.Ordinal);
    private readonly object _latestGate = new();
    private int _swept;

    /// <summary>Creates the release owner.</summary>
    /// <param name="userRoot">The per-user data root.</param>
    /// <param name="handler">An optional HTTP handler, the test seam.</param>
    internal GameToolReleases(string userRoot, HttpMessageHandler? handler)
    {
        _userRoot = userRoot;
        _client = new DownloadClient(handler);
        _releases = new HostedReleases(_client);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
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

    /// <summary>Whether opening a page should look the release up: nothing is known, or a failure has aged out.</summary>
    /// <param name="tool">The tool.</param>
    /// <returns>True when an unforced lookup is due.</returns>
    /// <remarks>A known release is kept for the session on page opens; installs and updates apply its lifetime.</remarks>
    internal bool LookupDue(GameToolDefinition tool)
    {
        return Cached(tool) is not { } cached
               || cached.Release is null && DateTime.UtcNow - cached.CheckedUtc >= FailureLifetime;
    }

    /// <summary>Looks up the latest release, reusing a recent answer or failure unless forced.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="force">Whether to bypass the cache.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The answer; a failure is carried in it rather than thrown.</returns>
    internal async Task<GameToolLatest> LatestAsync(GameToolDefinition tool, bool force,
        CancellationToken cancellationToken)
    {
        if (!force && Cached(tool) is { } cached
                   && DateTime.UtcNow - cached.CheckedUtc < (cached.Release is null ? FailureLifetime : LatestLifetime))
        {
            return cached;
        }

        GameToolLatest answer;
        try
        {
            var release = await _releases.FindAsync(tool.Source.Hosted(), tool.Architectures[0], cancellationToken)
                .ConfigureAwait(false);
            answer = new GameToolLatest(release, null, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
                                       or TimeoutException or IOException or KeyNotFoundException)
        {
            Log.Warn($"Game Tools: release lookup for {tool.Id} failed: {ex.Message}");
            answer = new GameToolLatest(null, ReleaseError(ex, tool.Source.Provider), DateTime.UtcNow);
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
    internal async Task<GameToolPackage> PrepareAsync(GameToolDefinition tool, HostedRelease release,
        Action<string, long, long> progress, CancellationToken cancellationToken)
    {
        SweepStages(tool);
        var sha = UpdateChecker.ParseHash(release.Asset.Sha256)
                  ?? throw new InvalidDataException(
                      "The publisher didn't provide a checksum for this download, so WSGM won't install it.");
        release = release with { Asset = release.Asset with { Sha256 = sha } };
        if (!Path.GetExtension(release.Asset.Name).TrimStart('.').Equals(tool.Source.PackageType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The published package is not the expected ." + tool.Source.PackageType
                                           + " archive.");
        }

        var identity = string.Join('|', release.Version, release.Asset.Name, sha, release.Asset.Url);
        var directory = PackageDirectory(tool, release.Version, sha);
        var payload = Path.Combine(directory, "payload");
        if (File.Exists(Path.Combine(directory, IdentityFile))
            && File.ReadAllText(Path.Combine(directory, IdentityFile)) == identity && Directory.Exists(payload))
        {
            try
            {
                return Validate(tool, release, Unwrap(payload)) with { Hashes = ReadHashes(directory) };
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
            await DownloadAsync(release.Asset, archive,
                copied => progress("Downloading " + tool.Name, copied, release.Asset.Size), cancellationToken)
                .ConfigureAwait(false);
            progress("Extracting", 0, 0);
            var extracted = Path.Combine(stage, "payload");
            await Task.Run(() => PackageArchive.Extract(archive, extracted, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            File.Delete(archive);
            var staged = Validate(tool, release, Unwrap(extracted));
            var hashes = await Task.Run(() => HashPayload(staged.Root), cancellationToken).ConfigureAwait(false);
            WriteHashes(stage, hashes);
            File.WriteAllText(Path.Combine(stage, IdentityFile), identity);

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

            GameToolStorage.DeleteTree(old);

            // The move keeps the validated contents; only the location of the unwrapped root changes.
            var unwrapped = Path.GetRelativePath(extracted, staged.Root);
            return staged with
            {
                Root = unwrapped == "." ? payload : Path.Combine(payload, unwrapped),
                Hashes = hashes
            };
        }
        finally
        {
            GameToolStorage.DeleteTree(stage);
        }
    }

    /// <summary>Validates an extracted payload against the definition.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="release">The release.</param>
    /// <param name="root">The payload root.</param>
    /// <returns>The package, without recorded hashes.</returns>
    /// <exception cref="InvalidDataException">The payload is incomplete or not the expected tool.</exception>
    internal static GameToolPackage Validate(GameToolDefinition tool, HostedRelease release, string root)
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
    internal static HostedRelease FromManifest(GameToolManifest manifest)
    {
        return new HostedRelease(manifest.ReleaseVersion, manifest.ReleaseId, "", "",
            new ReleaseAsset(manifest.AssetName, manifest.SourceUrl, manifest.AssetSha256));
    }

    private async Task DownloadAsync(ReleaseAsset asset, string destination, Action<long> copied,
        CancellationToken cancellationToken)
    {
        // Checked once here: an unsafe name is not a transfer failure and is never retried.
        if (!DownloadClient.IsSafeAssetName(asset.Name))
        {
            throw new InvalidDataException("The published package has an unsafe file name.");
        }

        // Three transfer attempts on transient failures, and a single verified re-download after a size or
        // hash mismatch. Both are bounded re-fetches of a verified file, not repeated writes to a game.
        var mismatches = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _client.DownloadAsync(asset, destination, cancellationToken, copied).ConfigureAwait(false);
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

    private static string ReleaseError(Exception ex, string provider)
    {
        return ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
            ? provider + " is limiting requests right now. Try again later."
            : ex is HttpRequestException or TimeoutException
                ? "Couldn't reach " + provider + " to check for releases."
                : "Couldn't read the published releases: " + ex.Message;
    }

    private static Dictionary<string, (string Sha256, long Size)> HashPayload(string root)
    {
        Dictionary<string, (string Sha256, long Size)> hashes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            hashes[GameToolPattern.Normalize(Path.GetRelativePath(root, file))] =
                (GameToolInstaller.Sha256(file), new FileInfo(file).Length);
        }

        return hashes;
    }

    private static void WriteHashes(string directory, IReadOnlyDictionary<string, (string Sha256, long Size)> hashes)
    {
        // Tab-separated: a file name can't contain a tab.
        var text = new StringBuilder();
        foreach (var (relative, (sha, size)) in hashes)
        {
            text.Append(sha).Append('\t').Append(size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(relative).Append('\n');
        }

        File.WriteAllText(Path.Combine(directory, HashesFile), text.ToString());
    }

    // A missing or unreadable record only costs hashing at install time, so it is never an error.
    private static Dictionary<string, (string Sha256, long Size)> ReadHashes(string directory)
    {
        Dictionary<string, (string Sha256, long Size)> hashes = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(directory, HashesFile)))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && UpdateChecker.ParseHash(parts[0]) is { } sha
                                      && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                                          out var size))
                {
                    hashes[parts[2]] = (sha, size);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hashes.Clear();
        }

        return hashes;
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
            GameToolStorage.DeleteTree(stale);
        }
    }
}
