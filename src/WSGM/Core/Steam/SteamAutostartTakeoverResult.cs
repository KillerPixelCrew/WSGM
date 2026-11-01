using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What a takeover attempt did.</summary>
/// <param name="Disabled">Sources this attempt turned off and recorded.</param>
/// <param name="Pending">Sources whose recording or write failed and remain pending.</param>
/// <param name="NeedsElevation">Sources left alone because this process cannot change them.</param>
public sealed record SteamAutostartTakeoverResult(
    IReadOnlyList<SteamAutostartSource> Disabled,
    IReadOnlyList<SteamAutostartSource> Pending,
    IReadOnlyList<SteamAutostartSource> NeedsElevation)
{
    /// <summary>Whether every source this attempt saw is now off.</summary>
    public bool Complete => Pending.Count == 0 && NeedsElevation.Count == 0;
}
