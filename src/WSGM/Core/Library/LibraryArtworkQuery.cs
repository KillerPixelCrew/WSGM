namespace WSGM.Core;

/// <summary>Source-produced artwork identification, captured away from UI publication.</summary>
/// <param name="ProviderId">The preferred provider for this source.</param>
/// <param name="PlatformId">The provider's platform identity, or zero when unmapped.</param>
/// <param name="ContentName">The original filename, when the provider indexes backing content.</param>
/// <param name="ContentSize">The observed file size, or zero when unavailable.</param>
public sealed record LibraryArtworkQuery(
    string ProviderId = "steamgriddb",
    int PlatformId = 0,
    string ContentName = "",
    long ContentSize = 0);
