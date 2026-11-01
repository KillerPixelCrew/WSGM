using System;
using System.Globalization;

namespace WSGM.Core;

/// <summary>The version stamp recorded beside a deployed shim.</summary>
/// <param name="SourceLength">Byte length of the staged payload when it was copied.</param>
/// <param name="SourceTicks">Last-write ticks of the staged payload when it was copied.</param>
/// <param name="DeployedLength">Byte length of the copy WSGM wrote.</param>
/// <param name="DeployedTicks">Last-write ticks of the copy WSGM wrote.</param>
/// <param name="Vector">Which name the copy was written under.</param>
internal readonly record struct SteamInputShimMarker(
    long SourceLength,
    long SourceTicks,
    long DeployedLength,
    long DeployedTicks,
    SteamInputShimVector Vector)
{
    /// <summary>Marker format this build writes and accepts.</summary>
    private const int MarkerFormatVersion = 1;

    /// <summary>Renders the stamp as the single line stored in the sidecar.</summary>
    /// <returns>The serialized stamp.</returns>
    public string Format()
    {
        return string.Join(
            ' ',
            $"WSGM-SIM/{MarkerFormatVersion}",
            SourceLength.ToString(CultureInfo.InvariantCulture),
            SourceTicks.ToString(CultureInfo.InvariantCulture),
            DeployedLength.ToString(CultureInfo.InvariantCulture),
            DeployedTicks.ToString(CultureInfo.InvariantCulture),
            Vector.ToString());
    }

    /// <summary>Parses a stamp written by <see cref="Format" />.</summary>
    /// <param name="line">The serialized stamp.</param>
    /// <param name="marker">The parsed stamp when parsing succeeded.</param>
    /// <returns><see langword="true" /> when the line is a stamp this build accepts.</returns>
    public static bool TryParse(string? line, out SteamInputShimMarker marker)
    {
        marker = default;
        if (line is null)
        {
            return false;
        }

        var parts = line.Trim().Split(' ');
        if (parts.Length != 6 || parts[0] != $"WSGM-SIM/{MarkerFormatVersion}")
        {
            return false;
        }

        if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var sourceLength)
            || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var sourceTicks)
            || !long.TryParse(parts[3], CultureInfo.InvariantCulture, out var deployedLength)
            || !long.TryParse(parts[4], CultureInfo.InvariantCulture, out var deployedTicks)
            || !Enum.TryParse<SteamInputShimVector>(parts[5], out var vector))
        {
            return false;
        }

        marker = new SteamInputShimMarker(sourceLength, sourceTicks, deployedLength, deployedTicks, vector);
        return true;
    }
}
