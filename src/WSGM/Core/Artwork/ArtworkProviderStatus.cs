namespace WSGM.Core;

/// <summary>Whether a provider can be searched, and what to say when it cannot.</summary>
/// <param name="Readiness">The provider's current state.</param>
/// <param name="Detail">A user-facing sentence when it is not <see cref="ArtworkProviderReadiness.Ready" />.</param>
internal readonly record struct ArtworkProviderStatus(ArtworkProviderReadiness Readiness, string Detail = "")
{
    /// <summary>Whether the provider may be searched.</summary>
    public bool IsReady => Readiness == ArtworkProviderReadiness.Ready;

    /// <summary>A ready status.</summary>
    public static ArtworkProviderStatus Ready { get; } = new(ArtworkProviderReadiness.Ready);
}
