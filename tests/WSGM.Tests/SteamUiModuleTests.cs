using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class SteamUiModuleTests
{
    [Fact]
    public void ModulesFlattenIntoThePatchPublicationAndCommandLookups()
    {
        SteamUiModuleSet set = new(
        [
            new SteamUiModule(
                "first",
                patches: [Patch("p.one"), Patch("p.two")],
                publications: [Publication("p.one")],
                commands: [Command("p.one", "go")]),
            new SteamUiModule(
                "second",
                patches: [Patch("p.three")],
                publications: [Publication("p.three")],
                commands: [Command("p.three", "go"), Command("p.three", "stop")]),
        ]);

        Assert.Equal(["p.one", "p.two", "p.three"], set.Patches.Select(patch => patch.Id));
        Assert.Equal(["p.one", "p.three"], set.Publications.Select(publication => publication.PatchId));
        Assert.True(set.TryGetCommand("p.three", "stop", out _));
    }

    [Fact]
    public void AnUnansweredCommandIsNotFound()
    {
        SteamUiModuleSet set = new([new SteamUiModule("only", commands: [Command("p", "go")])]);

        Assert.False(set.TryGetCommand("p", "stop", out _));
        Assert.False(set.TryGetCommand("other", "go", out _));
    }

    [Fact]
    public void TwoModulesSharingAnIdFailAtStartupRatherThanSilently()
    {
        // Startup, not first use: the alternative is a surface that half-exists because whichever
        // declaration won the race is the one that registered.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SteamUiModuleSet([new SteamUiModule("same"), new SteamUiModule("same")]));

        Assert.Contains("same", error.Message);
    }

    [Fact]
    public void TwoModulesRegisteringOnePatchNameBothInTheFailure()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SteamUiModuleSet(
            [
                new SteamUiModule("first", patches: [Patch("p.shared")]),
                new SteamUiModule("second", patches: [Patch("p.shared")]),
            ]));

        // Both names, because "duplicate patch" without saying who is a search through the file.
        Assert.Contains("p.shared", error.Message);
        Assert.Contains("second", error.Message);
    }

    [Fact]
    public void TwoModulesAnsweringOneCommandFailRatherThanOneWinning()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new SteamUiModuleSet(
            [
                new SteamUiModule("first", commands: [Command("p", "go")]),
                new SteamUiModule("second", commands: [Command("p", "go")]),
            ]));

        Assert.Contains("p/go", error.Message);
    }

    [Fact]
    public void ThePatchAndCommandNamespacesAreIndependent()
    {
        // A module may answer commands against a patch another module installs — the TDP row's
        // gate and the row that writes through it are separate patches sharing one id space.
        SteamUiModuleSet set = new(
        [
            new SteamUiModule("installer", patches: [Patch("p.one")]),
            new SteamUiModule("answerer", commands: [Command("p.one", "go")]),
        ]);

        Assert.Single(set.Patches);
        Assert.True(set.TryGetCommand("p.one", "go", out _));
    }

    [Fact]
    public void RefusedCarriesAReasonSoANoOpIsNeverSilent()
    {
        Assert.False(SteamUiCommandResult.Refused.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(SteamUiCommandResult.Refused.Error));
        Assert.True(SteamUiCommandResult.Applied.Succeeded);
    }

    private static ISteamUiPatch Patch(string id) => new StubPatch(id);

    private static SteamUiStatePublication Publication(string patchId) =>
        new(patchId, () => true, () => ValueTask.FromResult<JsonElement?>(null));

    private static SteamUiCommandHandler Command(string patchId, string command) =>
        new(patchId, command, (_, _) => Task.FromResult(SteamUiCommandResult.Applied));

    private sealed class StubPatch(string id) : ISteamUiPatch
    {
        public string Id => id;

        public int Version => 1;

        public SteamUiTargetRole TargetRole => SteamUiTargetRole.SharedJsContext;

        public string ResourceKey => id;

        public SteamUiPatchBounds Bounds => SteamUiPatchBounds.Default;

        public Task<SteamUiPatchProbeResult> ProbeAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SteamUiPatchOperationResult> ApplyAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SteamUiPatchOperationResult> VerifyAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SteamUiPatchOperationResult> RemoveAsync(
            SteamUiPatchContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
