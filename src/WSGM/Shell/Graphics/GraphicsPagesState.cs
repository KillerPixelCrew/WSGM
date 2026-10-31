using System.Collections.Generic;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The graphics settings pages of one projection revision.</summary>
/// <param name="Pages">The pages, in display order.</param>
/// <param name="Revision">Increases whenever the graphics projection changes.</param>
internal sealed record GraphicsPagesState(IReadOnlyList<SteamSettingsPage> Pages, long Revision);
