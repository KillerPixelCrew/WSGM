using System;
using System.Collections.Generic;
using System.Linq;

namespace WSGM.Core;

/// <summary>Which Xbox app build Game Mode needs and which offered build to install.</summary>
/// <remarks>
///     Xbox app 2612.1001.23.0 is the first build whose entry point honors the experience selector that
///     keeps it in its own Win32 dashboard (see <see cref="XboxAppExperience" />). Earlier builds always
///     hand off to a UWP app that Game Mode cannot show. The retail channel is preferred; the Xbox Insider
///     channel is used only while retail has no build that new.
/// </remarks>
internal static class XboxAppUpdatePlan
{
    /// <summary>The Xbox app's package family.</summary>
    internal const string PackageFamily = "Microsoft.GamingApp_8wekyb3d8bbwe";

    /// <summary>The Xbox app's Windows Update category in Microsoft's Store delivery service.</summary>
    internal const string CategoryId = "986a47b3-0085-4c0c-b3b3-3b806f969b00";

    /// <summary>The flight rings to ask, in order of preference.</summary>
    internal static readonly IReadOnlyList<string> Rings = ["Retail", "WIF"];

    /// <summary>The first build that opens its own dashboard.</summary>
    internal static readonly Version MinimumVersion = new(2612, 1001, 23, 0);

    /// <summary>Whether the installed Xbox app has to be updated before Game Mode can show it.</summary>
    /// <param name="installed">The installed version, or null when the Xbox app is not installed.</param>
    /// <returns>True for an installed build older than <see cref="MinimumVersion" />.</returns>
    internal static bool NeedsUpdate(Version? installed)
    {
        return installed is not null && installed < MinimumVersion;
    }

    /// <summary>Picks the build to install from one ring's offers.</summary>
    /// <param name="offers">What the ring offers.</param>
    /// <param name="installed">The installed version.</param>
    /// <returns>The newest Xbox app bundle that is new enough and newer than the installed build, or null.</returns>
    internal static StorePackageOffer? Choose(IEnumerable<StorePackageOffer> offers, Version? installed)
    {
        return offers
            .Where(offer => offer.IsNeutralBundle
                            && offer.Moniker.StartsWith("Microsoft.GamingApp_", StringComparison.Ordinal)
                            && offer.Moniker.EndsWith("_8wekyb3d8bbwe", StringComparison.Ordinal)
                            && offer.Version is { } version && version >= MinimumVersion
                            && (installed is null || version > installed))
            .OrderByDescending(offer => offer.Version)
            .FirstOrDefault();
    }
}
