using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class RunningApplicationCoordinatorTests
{
    [Fact]
    public void ProjectMapsOnlyActiveTruthfulExecutableTarget()
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            "steam:42",
            "game.exe");

        RtssApplicationTarget? target = RunningApplicationCoordinator.Project(snapshot);

        Assert.Equal("steam:42", target?.ApplicationId);
        Assert.Equal("game.exe", target?.RtssProfileName);
    }

    [Fact]
    public void ProjectClearsTargetWhenExecutableIdentityIsNotAuthoritative()
    {
        RunningApplicationTargetState[] states =
        [
            RunningApplicationTargetState.Global,
            RunningApplicationTargetState.IdentityOnly,
            RunningApplicationTargetState.Ambiguous,
            RunningApplicationTargetState.Unavailable,
        ];
        foreach (RunningApplicationTargetState state in states)
        {
            var snapshot = Snapshot(state, "steam:42", "stale.exe");
            Assert.Null(RunningApplicationCoordinator.Project(snapshot));
        }
    }

    [Theory]
    [InlineData(null, "game.exe")]
    [InlineData("steam:42", null)]
    [InlineData("", "game.exe")]
    [InlineData("steam:42", "")]
    public void ProjectRejectsIncompleteActiveTarget(string? applicationId, string? profileName)
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            applicationId,
            profileName);

        Assert.Null(RunningApplicationCoordinator.Project(snapshot));
    }

    private static RunningApplicationTargetSnapshot Snapshot(
        RunningApplicationTargetState state,
        string? applicationId,
        string? profileName) => new(
            1,
            1,
            state,
            applicationId,
            42,
            profileName is null ? null : $"C:\\Games\\{profileName}",
            profileName,
            DateTimeOffset.UnixEpoch,
            null);
}
