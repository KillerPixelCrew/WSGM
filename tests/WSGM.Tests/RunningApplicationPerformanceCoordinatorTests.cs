using System;
using WSGM.Core;
using WSGM.Shell;
using Xunit;

namespace WSGM.Tests;

public sealed class RunningApplicationPerformanceCoordinatorTests
{
    [Fact]
    public void ProjectMapsOnlyActiveTruthfulExecutableTarget()
    {
        var snapshot = Snapshot(
            RunningApplicationTargetState.Active,
            "steam:42",
            "game.exe");

        RtssApplicationTarget? target = RunningApplicationPerformanceCoordinator.Project(snapshot);

        Assert.Equal("steam:42", target?.ApplicationId);
        Assert.Equal("game.exe", target?.RtssProfileName);
    }

    [Theory]
    [InlineData(RunningApplicationTargetState.Global)]
    [InlineData(RunningApplicationTargetState.IdentityOnly)]
    [InlineData(RunningApplicationTargetState.Ambiguous)]
    [InlineData(RunningApplicationTargetState.Unavailable)]
    public void ProjectClearsTargetWhenExecutableIdentityIsNotAuthoritative(
        RunningApplicationTargetState state)
    {
        var snapshot = Snapshot(state, "steam:42", "stale.exe");

        Assert.Null(RunningApplicationPerformanceCoordinator.Project(snapshot));
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

        Assert.Null(RunningApplicationPerformanceCoordinator.Project(snapshot));
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
