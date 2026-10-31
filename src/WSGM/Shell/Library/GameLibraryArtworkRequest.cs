using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>A title the artwork stage should gather candidates for.</summary>
/// <param name="Id">The title's identity in the library.</param>
/// <param name="Name">The name the providers are searched with.</param>
/// <param name="Catalog">The images the title's own source offered.</param>
/// <param name="CatalogName">What to call that source's images, such as "Microsoft Store".</param>
/// <param name="Match">The game the user matched the title to, or null for the automatic match.</param>
/// <param name="PreferredProviderId">The source-appropriate provider searched before successful-empty fallback.</param>
/// <param name="PlatformId">The authoritative provider system identity, or zero when unmapped.</param>
/// <param name="RomName">The original backing filename used for ROM identification.</param>
/// <param name="RomSize">The observed backing file size, or zero when unavailable.</param>
internal sealed record GameLibraryArtworkRequest(
    string Id,
    string Name,
    IReadOnlyList<DiscoveredArtwork> Catalog,
    string CatalogName,
    ArtworkGameMatch? Match,
    string PreferredProviderId = "steamgriddb",
    int PlatformId = 0,
    string RomName = "",
    long RomSize = 0);
