using System;

namespace WSGM.Core;

/// <summary>What the last update check found, kept per user so Settings can show it.</summary>
public sealed record UpdateState
{
    /// <summary>When the last check completed.</summary>
    public DateTimeOffset? LastCheckUtc { get; init; }

    /// <summary>The newer release that check found, or null when WSGM was current.</summary>
    public UpdateOffer? Offer { get; init; }
}
