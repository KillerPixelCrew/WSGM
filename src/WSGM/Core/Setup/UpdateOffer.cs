using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A newer release, with what updating to it would cost.</summary>
/// <param name="Release">The release.</param>
/// <param name="Warnings">Installed community plugins that would stop loading.</param>
public sealed record UpdateOffer(UpdateRelease Release, IReadOnlyList<UpdateWarning> Warnings);
