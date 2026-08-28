using WSGM.Shell;

namespace WSGM.Tests;

public sealed class RunningApplicationTargetTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"wsgm-running-target-{Guid.NewGuid():N}");

    public RunningApplicationTargetTests() => Directory.CreateDirectory(_tempDirectory);

    [Fact]
    public void KnownSteamAppWithoutExecutableUsesIdentityButLeavesRtssGlobal()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        RunningApplicationTargetSnapshot initial = RunningApplicationTargetSnapshot.Initial(now);

        RunningApplicationTargetSnapshot target = RunningApplicationTargetProjection.Apply(
            initial,
            new SteamRunningAppObservation(true, [3280350], 7, null),
            new SteamRunningAppProfile(null, null, "Executable unavailable."),
            now);

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Equal("steam:3280350", target.ApplicationId);
        Assert.Equal((uint)3280350, target.SteamAppId);
        Assert.Null(target.RtssProfileName);
        Assert.Equal(1, target.Generation);
    }

    [Fact]
    public void ExitReturnsToGlobalWithoutInheritingPreviousApplication()
    {
        DateTimeOffset started = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        RunningApplicationTargetSnapshot active = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(started),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null),
            started);

        RunningApplicationTargetSnapshot exited = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(true, [], 3, null),
            null,
            started.AddMinutes(1));

        Assert.Equal(RunningApplicationTargetState.Global, exited.State);
        Assert.Null(exited.ApplicationId);
        Assert.Null(exited.SteamAppId);
        Assert.Null(exited.ExecutablePath);
        Assert.Null(exited.RtssProfileName);
        Assert.Equal(2, exited.Generation);
    }

    [Fact]
    public void UnreachableAndAmbiguousObservationsClearThePreviousTarget()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        RunningApplicationTargetSnapshot active = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null),
            now);

        RunningApplicationTargetSnapshot unavailable = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(false, [], 0, "CEF unavailable."),
            null,
            now.AddSeconds(1));
        RunningApplicationTargetSnapshot ambiguous = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(true, [42, 99], 3, null),
            null,
            now.AddSeconds(1));

        Assert.Equal(RunningApplicationTargetState.Unavailable, unavailable.State);
        Assert.Null(unavailable.ApplicationId);
        Assert.Null(unavailable.RtssProfileName);
        Assert.Equal(RunningApplicationTargetState.Ambiguous, ambiguous.State);
        Assert.Null(ambiguous.ApplicationId);
        Assert.Null(ambiguous.RtssProfileName);
    }

    [Fact]
    public void SourceGenerationReportsAStopStartEvenWhenTheAppIdIsTheSame()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
        SteamRunningAppProfile profile = new(@"D:\Games\game.exe", "game.exe", null);
        RunningApplicationTargetSnapshot first = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(now),
            new SteamRunningAppObservation(true, [42], 4, null),
            profile,
            now);

        RunningApplicationTargetSnapshot restarted = RunningApplicationTargetProjection.Apply(
            first,
            new SteamRunningAppObservation(true, [42], 6, null),
            profile,
            now.AddSeconds(1));

        Assert.Equal(first.Generation + 1, restarted.Generation);
        Assert.Equal(6, restarted.SourceGeneration);
    }

    [Fact]
    public void ExistingDirectShortcutYieldsOnlyItsExecutableProfileName()
    {
        string executable = Path.Combine(_tempDirectory, "shortcut-game.exe");
        File.WriteAllText(executable, "fixture");

        SteamRunningAppProfile profile = SteamRunningApplicationProbe.NormalizeShortcutTarget(
            $"\"{executable}\"");

        Assert.Equal(Path.GetFullPath(executable), profile.ExecutablePath);
        Assert.Equal("shortcut-game.exe", profile.RtssProfileName);
        Assert.Null(profile.Diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData(@"C:\Games\not-a-profile.dll")]
    [InlineData(@"C:\Program Files\WSGM\WSGM.Launch.exe")]
    public void UntruthfulShortcutTargetsNeverBecomeRtssProfiles(string target)
    {
        SteamRunningAppProfile profile = SteamRunningApplicationProbe.NormalizeShortcutTarget(target);

        Assert.Null(profile.ExecutablePath);
        Assert.Null(profile.RtssProfileName);
        Assert.NotNull(profile.Diagnostic);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
