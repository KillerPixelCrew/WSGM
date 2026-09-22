using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     Steam's per-game gear menu, which is the only way into the artwork browser.
/// </summary>
/// <remarks>
///     While artwork was a bundled package, the only thing that could put an entry here was an
///     admitted plugin, and that package never shipped: the menu rendered empty and the artwork
///     browser was unreachable. These tests pin the entry to WSGM itself so that cannot recur.
/// </remarks>
public sealed class SteamGameContextMenuBackendTests
{
    private static SteamGameContextMenuBackend WithArtwork(
        Func<uint, CancellationToken, Task<SteamUiCommandResult>>? open = null)
    {
        return new SteamGameContextMenuBackend(
            null,
            open ?? ((_, _) => Task.FromResult(SteamUiCommandResult.Applied)),
            appId => $"/wsgm/artwork/{appId}");
    }

    [Fact]
    public void TheArtworkEntryIsOfferedWithNoPluginInstalled()
    {
        // The regression this whole file exists for.
        var item = Assert.Single(WithArtwork().ReadState().Items);

        Assert.Equal(SteamGameContextMenuBackend.ArtworkId, item.Id);
        Assert.Equal("Change Artwork…", item.Label);
    }

    [Fact]
    public void ASessionWithoutArtworkOffersNothingRatherThanADeadEntry()
    {
        var backend = new SteamGameContextMenuBackend(null, null, null);

        Assert.Empty(backend.ReadState().Items);
    }

    [Fact]
    public async Task ActivatingTheArtworkEntryAnswersWithThePageRoute()
    {
        uint opened = 0;
        var backend = WithArtwork((appId, _) =>
        {
            opened = appId;
            return Task.FromResult(SteamUiCommandResult.Applied);
        });

        var result = await backend.ActivateAsync(2147483650u, SteamGameContextMenuBackend.ArtworkId,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2147483650u, opened);
        var route = result.Payload?.GetProperty("route").GetString();
        Assert.Equal("/wsgm/artwork/2147483650", route);
    }

    [Fact]
    public async Task ARefusedOpenKeepsItsReasonRatherThanNavigating()
    {
        // Navigating to a page the backend could not prepare would show an empty browser with no
        // explanation, which is the failure mode this contract exists to prevent.
        var backend = WithArtwork((_, _) =>
            Task.FromResult(new SteamUiCommandResult(false, "Steam did not identify the selected game.")));

        var result = await backend.ActivateAsync(0, SteamGameContextMenuBackend.ArtworkId,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("Steam did not identify the selected game.", result.Error);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task ActivatingArtworkWithoutArtworkIsRefusedRatherThanThrowing()
    {
        var backend = new SteamGameContextMenuBackend(null, null, null);

        var result = await backend.ActivateAsync(440, SteamGameContextMenuBackend.ArtworkId,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task AnUnknownEntryIsRefusedWithAReason()
    {
        var result = await WithArtwork().ActivateAsync(440, "someone.else", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task AReservedIdThisBuildDoesNotAnswerIsRefusedRatherThanPassedOn()
    {
        // A package must not be able to claim an entry WSGM stopped offering.
        var result = await WithArtwork().ActivateAsync(440, "wsgm.retired-entry", CancellationToken.None);

        Assert.False(result.Succeeded);
    }
}
