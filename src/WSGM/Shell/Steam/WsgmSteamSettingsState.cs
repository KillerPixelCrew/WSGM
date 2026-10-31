using System.Collections.Generic;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>What WSGM's settings page in Steam draws.</summary>
/// <param name="Pages">The sidebar's pages.</param>
/// <param name="Revision">Increases whenever what the page shows may have changed.</param>
internal sealed record WsgmSteamSettingsState(IReadOnlyList<SteamSettingsPage> Pages, long Revision);
