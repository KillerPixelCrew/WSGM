using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>One game in the Game Tools list.</summary>
/// <param name="AppId">The Steam AppID or non-Steam shortcut id.</param>
/// <param name="Name">The name the store last recorded; the list may show Steam's current name instead.</param>
/// <param name="IsShortcut">Whether the id is a non-Steam shortcut.</param>
/// <param name="Tools">Each installed tool, in catalog order.</param>
internal sealed record GameToolsGameSummary(
    uint AppId,
    string Name,
    bool IsShortcut,
    IReadOnlyList<GameToolsToolSummary> Tools);
