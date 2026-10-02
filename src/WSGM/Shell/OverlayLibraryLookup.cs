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
    internal static async Task<OverlayLibraryResult> ReadAsync(CancellationToken token = default)
    {
        try
        {
            var result = await SteamLibraryData.ReadGamesAsync(token);
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
