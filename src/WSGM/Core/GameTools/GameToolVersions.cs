using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace WSGM.Core.GameTools;

/// <summary>How an installed tool version relates to the latest published one.</summary>
internal enum GameToolVersionRelation
{
    /// <summary>One side is unknown or the two cannot be compared.</summary>
    Unknown,

    /// <summary>The installed copy is current.</summary>
    Current,

    /// <summary>A newer release, or a rebuild of the same number, is published.</summary>
    UpdateAvailable,

    /// <summary>The installed copy is newer than the latest stable release.</summary>
    NewerThanLatest
}

/// <summary>Version family, channel and ordering rules driven by catalog data.</summary>
internal static class GameToolVersions
{
    /// <summary>The first numeric token of a version string, such as <c>0.9.4</c>.</summary>
    /// <param name="version">A version, tag or product version.</param>
    /// <returns>The token, or an empty string.</returns>
    internal static string NumericToken(string? version)
    {
        var match = Regex.Match(version ?? "", @"\d+(?:\.\d+)*", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Value : "";
    }

    /// <summary>Parses the numeric token into integers, treating missing parts as zero when compared.</summary>
    /// <param name="version">The version text.</param>
    /// <returns>The integer parts, empty when there is no numeric token.</returns>
    internal static int[] Parts(string? version)
    {
        var token = NumericToken(version);
        return token.Length == 0
            ? []
            : token.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
    }

    /// <summary>Compares two versions as integer tuples.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>A negative, zero or positive value; zero when either has no numeric token.</returns>
    internal static int Compare(string? left, string? right)
    {
        var a = Parts(left);
        var b = Parts(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var difference = (index < a.Length ? a[index] : 0).CompareTo(index < b.Length ? b[index] : 0);
            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }

    /// <summary>Resolves the version family of a version string.</summary>
    /// <param name="tool">The definition carrying the family rules.</param>
    /// <param name="version">The version text.</param>
    /// <returns>The family id, or an empty string when none matches.</returns>
    internal static string Family(GameToolDefinition tool, string? version)
    {
        var token = NumericToken(version);
        if (token.Length == 0)
        {
            return "";
        }

        return tool.Families.FirstOrDefault(family => Regex.IsMatch(token, family.Pattern,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))?.Id ?? "";
    }

    /// <summary>Resolves the family from a binary's two version fields.</summary>
    /// <param name="tool">The definition.</param>
    /// <param name="info">The version resource.</param>
    /// <returns>The family, or an empty string when the fields disagree or neither resolves.</returns>
    internal static string Family(GameToolDefinition tool, PeVersionInfo info)
    {
        var file = Family(tool, info.FileVersion);
        var product = Family(tool, info.ProductVersion);
        if (file.Length > 0 && product.Length > 0 && file != product)
        {
            return "";
        }

        return product.Length > 0 ? product : file;
    }

    /// <summary>Resolves the channel from a product version.</summary>
    /// <param name="tool">The definition carrying channel tokens.</param>
    /// <param name="version">The product version.</param>
    /// <returns><c>preview</c>, <c>stable</c>, or an empty string.</returns>
    internal static string Channel(GameToolDefinition tool, string? version)
    {
        var text = version ?? "";
        if (tool.Channels.Preview.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return "preview";
        }

        return tool.Channels.Stable.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase))
            ? "stable"
            : "";
    }

    /// <summary>Relates an installed copy to the latest stable release.</summary>
    /// <param name="tool">The definition carrying the family rules.</param>
    /// <param name="installedVersion">The installed release's tag, or its product version when no tag was recorded.</param>
    /// <param name="installedReleaseId">The installed release identity.</param>
    /// <param name="latestVersion">The latest release version.</param>
    /// <param name="latestReleaseId">The latest release identity.</param>
    /// <returns>The relation.</returns>
    /// <remarks>
    ///     An equal number with a different release identity is a rebuild and counts as an update. An
    ///     installed copy newer than the latest stable is never offered an older stable as an update.
    ///     Two versions of one family numbered under different schemes, such as <c>0.10.x</c> and
    ///     <c>10.x</c>, can't be ordered and are unknown.
    /// </remarks>
    internal static GameToolVersionRelation Relate(GameToolDefinition tool, string installedVersion,
        string installedReleaseId, string? latestVersion, string? latestReleaseId)
    {
        var installed = Parts(installedVersion);
        var latest = Parts(latestVersion);
        if (string.IsNullOrEmpty(latestVersion) || installed.Length == 0 || latest.Length == 0)
        {
            return GameToolVersionRelation.Unknown;
        }

        var family = Family(tool, installedVersion);
        if (family.Length > 0 && family == Family(tool, latestVersion) && installed[0] != latest[0])
        {
            return GameToolVersionRelation.Unknown;
        }

        var order = Compare(installedVersion, latestVersion);
        if (order < 0)
        {
            return GameToolVersionRelation.UpdateAvailable;
        }

        if (order > 0)
        {
            return GameToolVersionRelation.NewerThanLatest;
        }

        return installedReleaseId.Length > 0 && !string.IsNullOrEmpty(latestReleaseId)
                                             && !installedReleaseId.Equals(latestReleaseId, StringComparison.Ordinal)
            ? GameToolVersionRelation.UpdateAvailable
            : GameToolVersionRelation.Current;
    }
}
