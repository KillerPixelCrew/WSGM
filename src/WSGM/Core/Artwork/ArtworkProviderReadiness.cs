namespace WSGM.Core;

/// <summary>Why a provider cannot be searched right now.</summary>
/// <remarks>
///     A disabled or unconfigured source is distinct from a successful search with no results.
/// </remarks>
internal enum ArtworkProviderReadiness
{
    /// <summary>Configured and usable.</summary>
    Ready,

    /// <summary>Turned off by the user.</summary>
    Disabled,

    /// <summary>Needs credentials that are not configured.</summary>
    MissingCredentials
}
