using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>A removable Steam library found on a mounted drive.</summary>
/// <param name="ContentId">The stable identity from the card's own marker.</param>
/// <param name="Name">
///     The display name to use when the card has never been
///     tracked and its marker carries no label.
/// </param>
/// <param name="AppIds">The app ids currently installed on the card.</param>
/// <param name="MarkerLabel">
///     The label in the card's own
///     <c>libraryfolder.vdf</c>, empty when it has none. Authoritative: it is the
///     only name that travels with the media.
/// </param>
/// <param name="CapacityBytes">The volume's size, 0 when Windows did not answer.</param>
/// <param name="FreeBytes">The volume's free space from the same reading.</param>
internal sealed record DiscoveredCard(
    string ContentId,
    string Name,
    List<long> AppIds,
    string MarkerLabel,
    long CapacityBytes = 0,
    long FreeBytes = 0);
