using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Overlay;

/// <summary>Which game's page Steam shows, for the Overlay rows that act on the game on screen.</summary>
internal static class SteamCurrentGame
{
    /// <summary>The game page Steam shows, or 0 when none does or Steam did not answer.</summary>
    /// <param name="steam">The session's Steam client, or null when there is none.</param>
    /// <returns>The AppID or shortcut id; 0 for the library root, no client or a failed read.</returns>
    internal static async Task<uint> AppIdAsync(SteamClient? steam)
    {
        if (steam is null)
        {
            return 0;
        }

        var page = await steam.CurrentPage.GetAsync();
        return page.Succeeded ? page.Value.AppId : 0;
    }
}
