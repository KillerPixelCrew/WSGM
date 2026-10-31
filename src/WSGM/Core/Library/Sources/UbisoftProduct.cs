using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>What the product cache says about one Ubisoft product.</summary>
/// <param name="UplayId">Its Ubisoft id.</param>
/// <param name="InstallId">Its install id.</param>
/// <param name="Name">Its name, localised when the cache names a token, or empty.</param>
/// <param name="Executable">Its executable relative to the install folder, or empty.</param>
/// <param name="ThirdParty">Whether another store sells and starts it.</param>
/// <param name="IsUlc">Whether it is downloadable content.</param>
/// <param name="HasStartGame">Whether it says how to start it at all.</param>
/// <param name="Addons">The ids of its add-ons, which are never games themselves.</param>
/// <param name="WorkingDirectory">
///     The executable's working folder relative to the install folder, empty for the install folder
///     itself, or null when the cache names none.
/// </param>
internal sealed record UbisoftProduct(
    uint UplayId,
    uint InstallId,
    string Name,
    string Executable,
    bool ThirdParty,
    bool IsUlc,
    bool HasStartGame,
    IReadOnlyList<uint> Addons,
    string? WorkingDirectory = null);
