using System.Text.Json;
using SteamUiToolkit;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamArtworkBrowserSurfaceTests
{
    private static readonly SteamUiCommandResult Refused = SteamUiCommandResult.Invalid("refused by the fake");

    private static SteamUiBridgeRequest Request(string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return new SteamUiBridgeRequest(1, "request", SteamArtworkBrowserSurface.PatchId, command, 1, 1, 1, 1,
            document.RootElement.Clone());
    }

    private static Task<SteamUiCommandResult> Send(ISteamUiModule module, string command, string payload)
    {
        var handler = module.Commands.Single(candidate => candidate.Command == command);
        return handler.Handle(Request(command, payload), CancellationToken.None);
    }

    [Fact]
    public void ThePageSpeaksTheSharedBrowseVocabulary()
    {
        var module = SteamArtworkBrowserSurface.Module(() => true,
            () => ValueTask.FromResult<SteamArtworkBrowserState?>(null), new RecordingBackend());
        var names = module.Commands.Select(command => command.Command).ToArray();

        Assert.Contains(SteamUiBrowseCommands.SetTab, names);
        Assert.Contains(SteamUiBrowseCommands.More, names);
        Assert.DoesNotContain("selectTab", names);
        Assert.DoesNotContain("loadMore", names);
        Assert.Equal(SteamArtworkBrowserSurface.Commands.Order(StringComparer.Ordinal),
            names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SetTabReachesTheBackendOnlyForAKnownTab()
    {
        var backend = new RecordingBackend();
        var module = SteamArtworkBrowserSurface.Module(() => true,
            () => ValueTask.FromResult<SteamArtworkBrowserState?>(null), backend);

        Assert.True((await Send(module, SteamUiBrowseCommands.SetTab, """{"tab":"hero"}""")).Succeeded);
        Assert.True((await Send(module, SteamUiBrowseCommands.SetTab, """{"tab":"manage"}""")).Succeeded);
        Assert.False((await Send(module, SteamUiBrowseCommands.SetTab, """{"tab":"elsewhere"}""")).Succeeded);
        Assert.False((await Send(module, SteamUiBrowseCommands.SetTab, """{"tab":"hero","x":1}""")).Succeeded);
        Assert.Equal(["tab:hero", "tab:manage"], backend.Calls);
    }

    [Fact]
    public async Task MoreAsksTheBackendForTheNextPage()
    {
        var backend = new RecordingBackend();
        var module = SteamArtworkBrowserSurface.Module(() => true,
            () => ValueTask.FromResult<SteamArtworkBrowserState?>(null), backend);

        Assert.True((await Send(module, SteamUiBrowseCommands.More, "{}")).Succeeded);
        Assert.Equal(["more"], backend.Calls);
    }

    private sealed class RecordingBackend : ISteamArtworkBrowserBackend
    {
        public List<string> Calls { get; } = [];

        public Task<SteamUiCommandResult> SelectTabAsync(string tab, CancellationToken cancellationToken)
        {
            Calls.Add("tab:" + tab);
            return Task.FromResult(SteamUiCommandResult.Applied);
        }

        public Task<SteamUiCommandResult> LoadMoreAsync(CancellationToken cancellationToken)
        {
            Calls.Add("more");
            return Task.FromResult(SteamUiCommandResult.Applied);
        }

        public Task<SteamUiCommandResult> ApplyAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> ApplyOfficialAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> ClearAsync(string tab, CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> ApplyLocalAsync(string tab, string path,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> ApplyInvisibleAsync(string tab, CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> SetFilterAsync(SteamArtworkBrowserFilter filter,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> SearchGamesAsync(string term, CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> SelectGameAsync(string? id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> SaveLogoPositionAsync(string anchor, int width, int height,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }

        public Task<SteamUiCommandResult> ResetLogoPositionAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Refused);
        }
    }
}
