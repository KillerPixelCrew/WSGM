using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>
///     The Steam client builds this WSGM version was checked with, from
///     <c>Core\Steam\steam-client-builds.json</c>, which <c>eng/check-steam-fingerprints.mjs --record</c>
///     maintains. Information only: no feature is gated on it.
/// </summary>
internal static class SteamClientBuilds
{
    private static readonly Lazy<IReadOnlyList<SteamClientBuild>> Bundled = new(LoadBundled);

    /// <summary>The builds shipped in this WSGM, parsed once.</summary>
    internal static IReadOnlyList<SteamClientBuild> Checked => Bundled.Value;

    /// <summary>Compares a client's build with a list of checked builds.</summary>
    /// <param name="build">The running client's build, or null when it could not be read.</param>
    /// <param name="known">The checked builds.</param>
    /// <returns>The comparison.</returns>
    internal static SteamClientBuildStatus Classify(SteamClientBuild? build, IReadOnlyList<SteamClientBuild> known)
    {
        if (build is null)
        {
            return SteamClientBuildStatus.Unreadable;
        }

        return known.Any(entry => entry.Channel == build.Channel && entry.Version == build.Version)
            ? SteamClientBuildStatus.Checked
            : SteamClientBuildStatus.Unchecked;
    }

    /// <summary>Parses the list's JSON document.</summary>
    /// <param name="file">The document.</param>
    /// <returns>The builds.</returns>
    /// <exception cref="InvalidDataException">An entry lacks its channel or version.</exception>
    internal static IReadOnlyList<SteamClientBuild> Parse(Stream file)
    {
        using var document = JsonDocument.Parse(file);
        List<SteamClientBuild> builds = [];
        foreach (var entry in document.RootElement.GetProperty("builds").EnumerateArray())
        {
            var channel = entry.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!
                : "";
            var version = entry.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : "";
            if (channel.Length == 0 || version.Length == 0)
            {
                throw new InvalidDataException("A checked Steam client build lacks its channel or version.");
            }

            var hash = entry.TryGetProperty("spContenthash", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
            builds.Add(new SteamClientBuild(channel, version, hash));
        }

        return builds;
    }

    private static IReadOnlyList<SteamClientBuild> LoadBundled()
    {
        using var embedded =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("WSGM.Core.Steam.steam-client-builds.json")
            ?? throw new InvalidDataException("The checked Steam client builds are missing from this build.");
        return Parse(embedded);
    }
}
