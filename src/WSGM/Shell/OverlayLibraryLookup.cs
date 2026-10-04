using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal sealed record OverlayLibraryResult(IReadOnlyList<SteamLibraryApp> Games, string? Error)
{
    internal bool Succeeded => Error is null;
}

internal static class OverlayLibraryLookup
{
    internal static async Task<OverlayLibraryResult> ReadAsync(SteamClient? steam, CancellationToken token = default)
    {
        if (steam is null)
        {
            return new OverlayLibraryResult([], "Steam is not available here.");
        }

        try
        {
            var result = await steam.Library.ReadGamesAsync(token);
            return new OverlayLibraryResult(result.Games, result.Error);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OverlayLibraryResult([], "Steam could not list the games: " + ex.Message);
        }
    }
}
