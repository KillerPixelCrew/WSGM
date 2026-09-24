using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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

    private const long MaxMetadataBytes = 1024 * 1024;
    private const long MaxSetupBytes = 1024L * 1024 * 1024;

    /// <summary>The per-user record of the last check.</summary>
    internal static string StatePath => Path.Combine(Log.Directory, "update.json");

    /// <summary>Where downloaded setups are kept until they run.</summary>
    internal static string DownloadDirectory => Path.Combine(InstallLayout.MachineData, "Updates");

    /// <summary>The running WSGM's version.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version);

    /// <summary>A client with a WSGM user agent, which GitHub's API requires, and a timeout long enough for the setup.</summary>
    public static HttpClient CreateHttpClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WSGM/{CurrentVersion}");
        return http;
    }

    /// <summary>Reads the last check, or an empty state when there is none.</summary>
    public static UpdateState ReadState(string? path = null)
    {
        try
        {
            var file = path ?? StatePath;
            return File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), UpdateJsonContext.Default.UpdateState) ?? new UpdateState()
                : new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateState();
        }
    }

    /// <summary>Records a check.</summary>
    public static void WriteState(UpdateState state, string? path = null)
    {
        var file = path ?? StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, UpdateJsonContext.Default.UpdateState));
        File.Move(temporary, file, true);
    }

    /// <summary>
    ///     Checks for a newer release and records the result. Offline, rate-limited or malformed
    ///     answers leave the previous state in place and return it.
    /// </summary>
    /// <param name="http">The client to use.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The recorded state.</returns>
    public static async Task<UpdateState> CheckAsync(HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            var release = ParseLatestRelease(await GetBytesAsync(http, LatestReleaseUrl, MaxMetadataBytes, cancellationToken)
                .ConfigureAwait(false));
            UpdateOffer? offer = null;
            if (release is not null && IsNewer(release.Version, CurrentVersion))
            {
                var warnings = release.BundleUrl is { } bundleUrl
                    ? Warnings(BundleManifest.TryRead(InstallLayout.InstalledBundle), InstalledPluginIds(),
                        BundleManifest.Parse(await GetBytesAsync(http, bundleUrl, BundleManifest.MaxBytes, cancellationToken)
                            .ConfigureAwait(false)))
                    : [];
                offer = new UpdateOffer(release, warnings);
                Log.Info($"Update: WSGM {release.Version} is available ({warnings.Count} plugin warning(s)).");
            }

            UpdateState state = new() { LastCheckUtc = DateTimeOffset.UtcNow, Offer = offer };
            WriteState(state);
            return state;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidDataException or IOException or UnauthorizedAccessException
                                   && !cancellationToken.IsCancellationRequested)
        {
            Log.Info("Update: the check did not complete: " + ex.Message);
            return ReadState();
        }
    }

    /// <summary>
    ///     Downloads the release's setup, verifies it against the release's SHA-256 and returns its path.
    ///     A setup that does not match is deleted, never run.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateRelease release, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(release);
        var hashText = System.Text.Encoding.UTF8.GetString(
            await GetBytesAsync(http, release.HashUrl, MaxMetadataBytes, cancellationToken).ConfigureAwait(false));
        var expected = ParseHash(hashText) ?? throw new InvalidDataException("The release's SHA-256 file is malformed.");

        Directory.CreateDirectory(DownloadDirectory);
        var target = Path.Combine(DownloadDirectory, Path.GetFileName(release.SetupName));
        var partial = target + ".partial";
        using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaxSetupBytes)
            {
                throw new InvalidDataException("The release's setup is larger than any WSGM setup can be.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxSetupBytes)
                {
                    throw new InvalidDataException("The release's setup is larger than any WSGM setup can be.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (length is > 0)
                {
                    progress?.Report((double)total / length.Value);
                }
            }
        }

        string actual;
        await using (var file = File.OpenRead(partial))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException("The downloaded setup does not match the release's SHA-256, so it was deleted.");
        }

        File.Move(partial, target, true);
        return target;
    }

    /// <summary>Starts the downloaded setup's quiet update; Windows asks for elevation when WSGM is not elevated.</summary>
    public static void RunSetup(string setupPath)
    {
        Log.Info("Update: starting " + setupPath + " /quiet /update");
        Process.Start(new ProcessStartInfo(setupPath, "/quiet /update") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Reads GitHub's latest-release answer. Drafts, prereleases and releases without a setup are ignored.</summary>
    internal static UpdateRelease? ParseLatestRelease(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object
            || Flag(root, "draft") || Flag(root, "prerelease")
            || !root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { Length: > 0 } tagName
            || !root.TryGetProperty("assets", out var assets) || assets.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        Dictionary<string, string> urls = new(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() is { } assetName
                && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { } assetUrl
                && assetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                urls[assetName] = assetUrl;
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
    internal static bool IsNewer(string candidate, Version current)
    {
        var core = candidate.Split('-', '+')[0];
        return Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed) && Normalize(parsed) > Normalize(current);
    }

    /// <summary>
    ///     The installed community plugins the next release does not carry. Their files stay, but the new
    ///     WSGM refuses them for their <c>wsgmVersion</c>, so the user is told who to ask before updating.
    /// </summary>
    internal static IReadOnlyList<UpdateWarning> Warnings(BundleManifest? installed, IReadOnlyCollection<string> installedIds,
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
    internal static string? ParseHash(string text)
    {
        var token = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
    }

    private static string[] InstalledPluginIds()
    {
        var catalog = PluginPackageCatalog.DiscoverInstalled();
        return
        [
            .. catalog.Common.Select(package => package.Manifest.Id),
            .. catalog.Device.InstalledPackage?.Manifest is { } device ? [device.Id] : Array.Empty<string>()
        ];
    }

    private static bool Flag(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient http, string url, long limit, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException($"{url} answered with more than {limit} bytes.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length > limit ? throw new InvalidDataException($"{url} answered with more than {limit} bytes.") : bytes;
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
