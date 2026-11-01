using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>One release channel of a tool: where its builds are published and how their versions are read.</summary>
/// <remarks>
///     A channel whose tags are not versions, such as a daily build tagged <c>nightly-20261031</c>, names the
///     version and build date in its asset instead, and <see cref="VersionPattern" /> reads them from there.
/// </remarks>
internal sealed record GameToolChannel
{
    /// <summary>The stable identity stored in a game's manifest, such as <c>stable</c> or <c>nightly</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>The name shown to the user.</summary>
    public string Label { get; init; } = "";

    /// <summary>What the channel offers, shown beside the choice; empty for none.</summary>
    public string Description { get; init; } = "";

    /// <summary>Where its packages are published.</summary>
    public GameToolSource Source { get; init; } = new();

    /// <summary>
    ///     A pattern over the asset name with a <c>version</c> group and an optional eight-digit <c>build</c>
    ///     date group; empty when the release tag is the version.
    /// </summary>
    public string VersionPattern { get; init; } = "";

    /// <summary>Applies the channel's version rule to a release found through its source.</summary>
    /// <param name="release">The release as the hosted lookup answered it.</param>
    /// <returns>The release with the version the asset names, or unchanged when the tag is the version.</returns>
    /// <exception cref="InvalidDataException">The asset name does not carry a version.</exception>
    internal HostedRelease Normalize(HostedRelease release)
    {
        if (VersionPattern.Length == 0)
        {
            return release;
        }

        var match = Match(release.Asset.Name);
        if (!match.Success || match.Groups["version"].Value.Length == 0)
        {
            throw new InvalidDataException("The published " + Label.ToLowerInvariant()
                                                            + " package does not name its version.");
        }

        return release with { Version = match.Groups["version"].Value };
    }

    /// <summary>The build date an asset name carries, or null when the channel names none.</summary>
    /// <param name="assetName">The asset file name.</param>
    /// <returns>The date, never invented from anything else.</returns>
    internal DateOnly? BuildDate(string assetName)
    {
        if (VersionPattern.Length == 0 || assetName.Length == 0)
        {
            return null;
        }

        var match = Match(assetName);
        return match.Success && match.Groups["build"].Success
                             && DateOnly.TryParseExact(match.Groups["build"].Value, "yyyyMMdd",
                                 CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>A version as the user reads it: the number and, for a dated build, the channel and its date.</summary>
    /// <param name="version">The release version.</param>
    /// <param name="assetName">The asset it came from, for the build date.</param>
    /// <returns>Such as <c>0.9.4</c> or <c>10.0.0-pre1 · nightly 31 Oct 2026</c>.</returns>
    internal string Display(string version, string assetName)
    {
        return BuildDate(assetName) is { } date
            ? version + " · " + Label.ToLowerInvariant() + " "
              + date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : version;
    }

    private Match Match(string assetName)
    {
        return Regex.Match(assetName, VersionPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
