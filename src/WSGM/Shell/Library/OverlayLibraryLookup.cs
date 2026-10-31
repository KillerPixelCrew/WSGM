using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal static class OverlayLibraryLookup
{
    internal static async Task<SteamLibraryReadResult> ReadAsync(SteamClient? steam, CancellationToken token = default)
    {
        if (steam is null)
        {
            return new SteamLibraryReadResult([], "Steam is not available here.");
        }

        try
        {
            return await steam.Library.ReadGamesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SteamLibraryReadResult([], "Steam could not list the games: " + ex.Message);
        }
    }
}
