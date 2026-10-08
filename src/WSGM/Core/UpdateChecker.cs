using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Install;

namespace WSGM.Core;

/// <summary>A published WSGM release and the files the updater needs from it.</summary>
/// <param name="Version">The release version, without the tag's <c>v</c>.</param>
/// <param name="PageUrl">The release page, for the notes.</param>
/// <param name="SetupName">The setup's file name.</param>
/// <param name="SetupUrl">Where to download the setup.</param>
/// <param name="HashUrl">Where to download the setup's SHA-256 file.</param>
/// <param name="BundleUrl">Where to download the release's bundle manifest, when it has one.</param>
public sealed record UpdateRelease(
    string Version,
    string PageUrl,
    string SetupName,
    string SetupUrl,
    string HashUrl,
    string? BundleUrl);

/// <summary>An installed community plugin the release no longer carries.</summary>
/// <param name="PluginId">The plugin id.</param>
/// <param name="Name">The plugin's display name.</param>
/// <param name="Contact">The developer contact, when the plugin names one.</param>
/// <param name="Log">The failed build's log, when the release recorded it.</param>
public sealed record UpdateWarning(string PluginId, string Name, string? Contact, string? Log);

/// <summary>A newer release, with what updating to it would cost.</summary>
/// <param name="Release">The release.</param>
/// <param name="Warnings">Installed community plugins that would stop loading.</param>
public sealed record UpdateOffer(UpdateRelease Release, IReadOnlyList<UpdateWarning> Warnings);

/// <summary>What the last update check found, kept per user so Settings can show it.</summary>
public sealed record UpdateState
{
    /// <summary>When the last check completed.</summary>
    public DateTimeOffset? LastCheckUtc { get; init; }

    /// <summary>The newer release that check found, or null when WSGM was current.</summary>
    public UpdateOffer? Offer { get; init; }
}

/// <summary>
///     Finds newer WSGM releases and hands one to its setup. It only ever reads GitHub's latest
///     release, which is never a prerelease; offline it fails quietly. Applying an update is always
///     the user's action, because the setup closes Steam and WSGM.
/// </summary>
public static class UpdateChecker
{
    /// <summary>The latest-release endpoint of the WSGM repository.</summary>
    internal const string LatestReleaseUrl = "https://api.github.com/repos/KillerPixelCrew/WSGM/releases/latest";

    /// <summary>Where downloaded setups are kept until they run.</summary>
    internal static string DownloadDirectory => Path.Combine(InstallLayout.MachineData, "Updates");

    /// <summary>The running WSGM's version.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version);

    /// <summary>The per-user record of the last check.</summary>
    /// <param name="context">Interactive user-data root for this process owner.</param>
    /// <returns>The update.json path; no directory or file is created.</returns>
    internal static string StatePath(UserDataContext context)
    {
        return Path.Combine(context.Root, "update.json");
    }

    /// <summary>A client with a WSGM user agent, which GitHub's API requires, and a timeout long enough for the setup.</summary>
    /// <returns>A new caller-owned client with a ten-minute request timeout; dispose it when the updater owner ends.</returns>
    public static HttpClient CreateHttpClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WSGM/{CurrentVersion}");
        return http;
    }

    /// <summary>Reads the last check, or an empty state when there is none.</summary>
    /// <param name="path">The explicit update-state file to read.</param>
    /// <returns>The saved state, or a new empty state for missing, unreadable, or malformed JSON.</returns>
    public static UpdateState ReadState(string path)
    {
        try
        {
            var file = path;
            return File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), UpdateJsonContext.Default.UpdateState) ??
                  new UpdateState()
                : new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    /// <summary>Records a check.</summary>
    /// <param name="state">The check result to record.</param>
    /// <param name="path">The explicit update-state file to write.</param>
    public static void WriteState(UpdateState state, string path)
    {
        var file = path;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicFile.WriteText(file, JsonSerializer.Serialize(state, UpdateJsonContext.Default.UpdateState), false);
    }

    /// <summary>
    ///     Checks for a newer release and records the result. Offline, rate-limited or malformed
    ///     answers leave the previous state in place and return it.
    /// </summary>
    /// <param name="http">The client to use.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The recorded state.</returns>
    /// <param name="context">The owner's update-state directory.</param>
    public static async Task<UpdateState> CheckAsync(HttpClient http, UserDataContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            var release = ParseLatestRelease(
                await GetBytesAsync(http, LatestReleaseUrl, cancellationToken)
                    .ConfigureAwait(false));
            UpdateOffer? offer = null;
            if (release is not null && IsNewer(release.Version, CurrentVersion))
            {
                var warnings = release.BundleUrl is { } bundleUrl
                    ? Warnings(BundleManifest.TryRead(InstallLayout.InstalledBundle), InstalledPluginIds(),
                        BundleManifest.Parse(
                            await GetBytesAsync(http, bundleUrl, cancellationToken)
                                .ConfigureAwait(false)))
                    : [];
                offer = new UpdateOffer(release, warnings);
                Log.Info($"Update: WSGM {release.Version} is available ({warnings.Count} plugin warning(s)).");
            }

            UpdateState state = new() { LastCheckUtc = DateTimeOffset.UtcNow, Offer = offer };
            WriteState(state, StatePath(context));
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidDataException or IOException or UnauthorizedAccessException
                                   && !cancellationToken.IsCancellationRequested)
        {
            Log.Info("Update: the check did not complete: " + ex.Message);
            return ReadState(StatePath(context));
        }
    }

    /// <summary>
    ///     Downloads the release's setup, verifies it against the release's SHA-256 and returns its path.
    ///     A setup that does not match is never promoted or run; partial-file cleanup is best effort.
    /// </summary>
    /// <param name="http">Borrowed HTTP client; left open after the download.</param>
    /// <param name="release">Release asset URLs, expected setup filename, and hash-file location.</param>
    /// <param name="progress">Optional fraction from 0 to 1 when the server supplies Content-Length.</param>
    /// <param name="cancellationToken">Cancels network, file writes, and hash verification.</param>
    /// <returns>The verified setup path in the machine update directory; the setup is not launched.</returns>
    /// <exception cref="InvalidDataException">The hash file is malformed or the downloaded setup fails its hash check.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the transfer.</exception>
    /// <remarks>HTTP and file failures propagate; the partial file is removed on a best-effort basis.</remarks>
    public static Task<string> DownloadAsync(HttpClient http, UpdateRelease release, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        return DownloadAsync(http, release, progress, cancellationToken, DownloadDirectory);
    }

    /// <summary>Downloads and verifies setup into an explicit directory before promoting the partial file.</summary>
    /// <param name="http">Borrowed HTTP client; never disposed here.</param>
    /// <param name="release">Asset URLs and setup filename; only its basename is used under the destination.</param>
    /// <param name="progress">Optional transfer fraction, reported only when Content-Length is positive.</param>
    /// <param name="cancellationToken">Cancels network, file writes, and SHA-256 calculation.</param>
    /// <param name="downloadDirectory">
    ///     Destination directory to create when needed; callers must serialize same-filename
    ///     downloads.
    /// </param>
    /// <param name="stallTimeout">Maximum silence for each response-body read, or null for the shared default.</param>
    /// <returns>The final verified path, replacing an earlier same-named setup only after verification.</returns>
    /// <exception cref="InvalidDataException">Hash metadata is invalid or setup bytes do not match it.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the operation.</exception>
    /// <remarks>Partial-file cleanup is best effort; HTTP and filesystem failures propagate.</remarks>
    internal static async Task<string> DownloadAsync(HttpClient http, UpdateRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken, string downloadDirectory, TimeSpan? stallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        Directory.CreateDirectory(downloadDirectory);
        var target = Path.Combine(downloadDirectory, Path.GetFileName(release.SetupName));
        FileCleanup.TryDelete(target + ".partial");
        var hashText = Encoding.UTF8.GetString(
            await GetBytesAsync(http, release.HashUrl, cancellationToken, stallTimeout).ConfigureAwait(false));
        var expected = ParseHash(hashText) ??
                       throw new InvalidDataException("The release's SHA-256 file is malformed.");

        long? length = null;
        try
        {
            await VerifiedDownload.WriteAsync(async token =>
            {
                var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                length = response.Content.Headers.ContentLength;
                return response;
            }, target, expected, 0, cancellationToken, total =>
            {
                if (length is > 0)
                {
                    progress?.Report(Math.Min(1, (double)total / length.Value));
                }
            }, stallTimeout).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                "The downloaded setup does not match the release's SHA-256, so it was deleted.", ex);
        }

        return target;
    }

    /// <summary>Starts the downloaded setup's quiet update; Windows asks for elevation when WSGM is not elevated.</summary>
    /// <param name="setupPath">Previously verified setup path; this method does not rehash it.</param>
    /// <remarks>Dispatches /quiet /update without waiting for completion; shell activation failures propagate.</remarks>
    public static void RunSetup(string setupPath)
    {
        Log.Info("Update: starting " + setupPath + " /quiet /update");
        Process.Start(new ProcessStartInfo(setupPath, "/quiet /update") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Reads GitHub's latest-release answer. Drafts, prereleases and releases without a setup are ignored.</summary>
    /// <param name="utf8Json">UTF-8 GitHub release-response bytes.</param>
    /// <returns>Stable release metadata with HTTPS setup and hash assets, or null when required assets are absent.</returns>
    /// <exception cref="JsonException">The response is not valid JSON.</exception>
    /// <exception cref="InvalidOperationException">A release field has an incompatible JSON kind.</exception>
    internal static UpdateRelease? ParseLatestRelease(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object
            || JsonRead.Flag(root, "draft") || JsonRead.Flag(root, "prerelease")
            || !root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } tagName
            || !root.TryGetProperty("assets", out var assets) || assets.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        Dictionary<string, string> urls = new(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = JsonRead.OptionalString(asset, "name");
            var assetUrl = JsonRead.OptionalString(asset, "browser_download_url");
            if (assetName is not null && HttpUrls.IsHttps(assetUrl))
            {
                urls[assetName] = assetUrl!;
            }
        }

        var version = tagName.TrimStart('v', 'V');
        var setupName = $"WSGM-Setup-{version}.exe";
        if (!urls.TryGetValue(setupName, out var setupUrl) || !urls.TryGetValue(setupName + ".sha256", out var hashUrl))
        {
            return null;
        }

        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new UpdateRelease(version, page, setupName, setupUrl, hashUrl, urls.GetValueOrDefault("bundle.json"));
    }

    /// <summary>Whether a release version is newer than the running one. An unreadable version never is.</summary>
    /// <param name="candidate">Release version text; suffixes after - or + are ignored.</param>
    /// <param name="current">Installed version; build revision is ignored for comparison.</param>
    /// <returns>True when the parseable normalized release version is greater; false for invalid text.</returns>
    internal static bool IsNewer(string candidate, Version current)
    {
        var core = candidate.Split('-', '+')[0];
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed) &&
               Normalize(parsed) > Normalize(current);
    }

    /// <summary>
    ///     The installed community plugins the next release does not carry. Their files stay, but the new
    ///     WSGM refuses them for their <c>wsgmVersion</c>, so the user is told who to ask before updating.
    /// </summary>
    /// <param name="installed">Current release bundle, or null when provenance is unavailable.</param>
    /// <param name="installedIds">Plugin identities currently installed.</param>
    /// <param name="next">Candidate release bundle and omitted-plugin diagnostics.</param>
    /// <returns>Warnings for installed community IDs missing from the candidate bundle; empty without current provenance.</returns>
    internal static IReadOnlyList<UpdateWarning> Warnings(BundleManifest? installed,
        IReadOnlyCollection<string> installedIds,
        BundleManifest next)
    {
        if (installed is null)
        {
            return [];
        }

        List<UpdateWarning> warnings = [];
        foreach (var plugin in installed.Plugins.Where(plugin => plugin.Community && installedIds.Contains(plugin.Id)))
        {
            if (next.Plugins.Any(candidate => candidate.Id == plugin.Id))
            {
                continue;
            }

            var outdated = next.Outdated.FirstOrDefault(entry => entry.Id == plugin.Id);
            warnings.Add(new UpdateWarning(plugin.Id, plugin.Name, outdated?.Contact ?? plugin.Contact, outdated?.Log));
        }

        return warnings;
    }

    /// <summary>Reads the first token of a <c>.sha256</c> file.</summary>
    /// <param name="text">Hash-file text, optionally followed by a filename.</param>
    /// <returns>The first 64-hex-character token in lowercase, or null when malformed.</returns>
    internal static string? ParseHash(string text)
    {
        var token = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
    }

    private static string[] InstalledPluginIds()
    {
        var catalog = PluginPackageCatalog.Discover(InstallLayout.Plugins);
        return
        [
            .. catalog.Common.Select(package => package.Manifest.Id),
            .. catalog.Device.InstalledPackage?.Manifest is { } device ? [device.Id] : Array.Empty<string>()
        ];
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient http, string url,
        CancellationToken cancellationToken, TimeSpan? stallTimeout = null)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = await BoundedHttp.ReadAsync(response.Content, int.MaxValue,
            static () => new InvalidDataException("The answer cannot be represented by a memory stream."),
            cancellationToken, stallTimeout).ConfigureAwait(false);
        return body.ToArray();
    }

    private static Version Normalize(Version? version)
    {
        return version is null
            ? new Version(0, 0, 0)
            : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }
}

/// <summary>Source-generated metadata for the update state file.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateState))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
