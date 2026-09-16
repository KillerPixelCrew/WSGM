using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class RunningApplicationTargetTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"wsgm-running-target-{Guid.NewGuid():N}");

    public RunningApplicationTargetTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void KnownSteamAppWithoutExecutableUsesIdentityButLeavesRtssGlobal()
    {
        var initial = RunningApplicationTargetSnapshot.Initial();

        var target = RunningApplicationTargetProjection.Apply(
            initial,
            new SteamRunningAppObservation(true, [3280350], 7, null),
            new SteamRunningAppProfile(null, null, "Executable unavailable."));

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Equal("steam:3280350", target.ApplicationId);
        Assert.Equal((uint)3280350, target.SteamAppId);
        Assert.Null(target.RtssProfileName);
        Assert.Equal(1, target.Generation);
    }

    [Fact]
    public void ExitReturnsToGlobalWithoutInheritingPreviousApplication()
    {
        var active = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null));

        var exited = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(true, [], 3, null),
            null);

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
        var active = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null));

        var unavailable = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(false, [], 0, "CEF unavailable."),
            null);
        var ambiguous = RunningApplicationTargetProjection.Apply(
            active,
            new SteamRunningAppObservation(true, [42, 99], 3, null),
            null);

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
        SteamRunningAppProfile profile = new(@"D:\Games\game.exe", "game.exe", null);
        var first = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 4, null),
            profile);

        var restarted = RunningApplicationTargetProjection.Apply(
            first,
            new SteamRunningAppObservation(true, [42], 6, null),
            profile);

        Assert.Equal(first.Generation + 1, restarted.Generation);
        Assert.Equal(6, restarted.SourceGeneration);
    }

    [Fact]
    public void ExistingDirectShortcutYieldsOnlyItsExecutableProfileName()
    {
        var executable = Path.Combine(_tempDirectory, "shortcut-game.exe");
        File.WriteAllText(executable, "fixture");

        var profile = SteamRunningApplicationProbe.NormalizeShortcutTarget(
            $"\"{executable}\"");

        Assert.Equal(Path.GetFullPath(executable), profile.ExecutablePath);
        Assert.Equal("shortcut-game.exe", profile.RtssProfileName);
        Assert.Null(profile.Diagnostic);
    }

    [Fact]
    public void AnExistingAbsoluteInstallFolderBecomesPairingEvidence()
    {
        var profile =
            SteamRunningApplicationProbe.NormalizeInstallFolder(_tempDirectory);

        Assert.Equal(Path.GetFullPath(_tempDirectory), profile.InstallFolder);
        Assert.Null(profile.RtssProfileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"steamapps\common\Game")]
    [InlineData(@"Q:\definitely\not\present")]
    public void UntruthfulInstallFoldersProduceNoPairingEvidence(string folder)
    {
        var profile =
            SteamRunningApplicationProbe.NormalizeInstallFolder(folder);

        Assert.Null(profile.InstallFolder);
        Assert.NotNull(profile.Diagnostic);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData(@"C:\Games\not-a-profile.dll")]
    [InlineData(@"C:\Program Files\WSGM\WSGM.Launch.exe")]
    public void UntruthfulShortcutTargetsNeverBecomeRtssProfiles(string target)
    {
        var profile = SteamRunningApplicationProbe.NormalizeShortcutTarget(target);

        Assert.Null(profile.ExecutablePath);
        Assert.Null(profile.RtssProfileName);
        Assert.NotNull(profile.Diagnostic);
    }

    [Fact]
    public void UnresolvedShortcutProfileIsRetriedAfterItsBackoff()
    {
        const uint shortcutAppId = 0x8000002A;
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        SteamRunningAppProfile unresolved = new(null, null, "Transient CEF failure.");

        Assert.False(RunningApplicationMonitor.ShouldResolveProfile(
            shortcutAppId,
            shortcutAppId,
            unresolved,
            now,
            now.AddSeconds(1)));
        Assert.True(RunningApplicationMonitor.ShouldResolveProfile(
            shortcutAppId,
            shortcutAppId,
            unresolved,
            now.AddSeconds(1),
            now.AddSeconds(1)));
    }

    [Fact]
    public void ForegroundApplicationSuppliesTheIdentitySteamDoesNotHave()
    {
        // The whole point of the second source: on the desktop, or for a title Steam never
        // launched, per-application policy still has something to key on.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [], 3, null),
            null,
            new ForegroundApplicationObservation("Cyberpunk2077.exe"));

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("process:cyberpunk2077.exe", target.ApplicationId);
        Assert.Equal("Cyberpunk2077.exe", target.RtssProfileName);
        Assert.Null(target.SteamAppId);
    }

    [Fact]
    public void SteamsIdentityOutranksTheForegroundWindow()
    {
        // Alt-tabbing out of a running Steam game must not retarget its profile: Steam's identity
        // is the one the launch went through and the one the RTSS profile was resolved from.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(@"D:\Games\game.exe", "game.exe", null),
            new ForegroundApplicationObservation("chrome.exe"));

        Assert.Equal("steam:42", target.ApplicationId);
        Assert.Equal("game.exe", target.RtssProfileName);
    }

    [Fact]
    public void ForegroundSuppliesOrdinarySteamGamesMissingRtssProfile()
    {
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable.",
                @"D:\SteamLibrary\steamapps\common\Game"),
            new ForegroundApplicationObservation(
                "game.exe",
                @"D:\SteamLibrary\steamapps\common\Game\bin\game.exe"));

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("steam:42", target.ApplicationId);
        Assert.Equal((uint)42, target.SteamAppId);
        Assert.Equal("game.exe", target.RtssProfileName);
        Assert.Equal(
            @"D:\SteamLibrary\steamapps\common\Game\bin\game.exe",
            target.ExecutablePath);
    }

    [Fact]
    public void AForegroundOutsideTheInstallFolderNeverBecomesTheGamesProfile()
    {
        // The bug this rule exists for: a terminal focused while a store title was resolving became
        // HITMAN 3's sticky RTSS target, and the frame limit landed on WindowsTerminal.exe
        // (device-observed 2026-09-02).
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(
                null,
                null,
                "Steam exposes no executable.",
                @"D:\SteamLibrary\steamapps\common\Game"),
            new ForegroundApplicationObservation(
                "WindowsTerminal.exe",
                @"C:\Program Files\WindowsApps\Terminal\WindowsTerminal.exe"));

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Fact]
    public void AStoreTitleWithoutAKnownInstallFolderStaysIdentityOnly()
    {
        // No folder means no proof; a bare foreground name pairing here is how the wrong
        // application captured a game's profile for its whole run.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42], 2, null),
            new SteamRunningAppProfile(null, null, "Install folder still resolving."),
            new ForegroundApplicationObservation("game.exe", @"D:\Games\game.exe"));

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Fact]
    public void RtssRenderingProvesTheGameWhenSteamKnowsNoInstallFolder()
    {
        // Skyrim SE through Mod Organizer: Steam names AppID 489830 as running and knows nothing
        // else about it — empty install folder, empty launch options, no local content — so the
        // folder proof can never be satisfied and its per-application profile was never written
        // (Claw, 2026-09-04). RTSS hooking and drawing the process is the proof that remains, and
        // it is the one that matters: a profile for something RTSS does not render does nothing.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [489830], 2, null),
            new SteamRunningAppProfile(null, null, "Steam did not report the install folder."),
            new ForegroundApplicationObservation(
                "SkyrimSE.exe",
                @"D:\Modding\Skyrim\SkyrimSE.exe",
                4321),
            [new RtssFrametimeSample(4321, @"D:\Modding\Skyrim\SkyrimSE.exe", 8.3, 120, 40)]);

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("steam:489830", target.ApplicationId);
        Assert.Equal("SkyrimSE.exe", target.RtssProfileName);
        Assert.Equal(@"D:\Modding\Skyrim\SkyrimSE.exe", target.ExecutablePath);
    }

    [Fact]
    public void AFocusedApplicationRtssIsNotRenderingIsStillNotTheGame()
    {
        // The other half of the same run: Waterfox, Mod Organizer, GameBar and RustDesk all held
        // focus while Skyrim was up. None is hooked, so none may take the pairing — the rendering
        // set is proof, not a second bare name.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [489830], 2, null),
            new SteamRunningAppProfile(null, null, "Steam did not report the install folder."),
            new ForegroundApplicationObservation(
                "waterfox.exe",
                @"C:\Program Files\Waterfox\waterfox.exe",
                777),
            [new RtssFrametimeSample(4321, @"D:\Modding\Skyrim\SkyrimSE.exe", 8.3, 120, 40)]);

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Fact]
    public void AnUnreadableForegroundProcessIdNeverMatchesTheRenderingSet()
    {
        // Zero is "could not be read", not a process. Matching it against an entry would pair the
        // game with whatever RTSS happened to list first.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [489830], 2, null),
            new SteamRunningAppProfile(null, null, "Steam did not report the install folder."),
            new ForegroundApplicationObservation("game.exe", @"D:\Games\game.exe"),
            [new RtssFrametimeSample(0, @"D:\Games\other.exe", 8.3, 120, 40)]);

        Assert.Equal(RunningApplicationTargetState.IdentityOnly, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Fact]
    public void AnUnresolvedShortcutStillTakesTheForegroundName()
    {
        // A shortcut has no install folder to check, and its target resolution normally names the
        // executable outright; the rare unresolved one keeps the name-based fill.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [0x8000002A], 2, null),
            new SteamRunningAppProfile(null, null, "The shortcut target is a script."),
            new ForegroundApplicationObservation("game.exe"));

        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("game.exe", target.RtssProfileName);
    }

    [Fact]
    public void ForegroundResolvedSteamProfileSurvivesAltTab()
    {
        SteamRunningAppObservation observation = new(true, [42], 2, null);
        SteamRunningAppProfile unresolved = new(
            null,
            null,
            "Steam exposes no executable.",
            @"D:\SteamLibrary\steamapps\common\Game");
        var game = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            observation,
            unresolved,
            new ForegroundApplicationObservation(
                "game.exe",
                @"D:\SteamLibrary\steamapps\common\Game\game.exe"));

        var altTabbed = RunningApplicationTargetProjection.Apply(
            game,
            observation,
            unresolved,
            new ForegroundApplicationObservation(
                "chrome.exe",
                @"C:\Program Files\Google\Chrome\chrome.exe"));

        Assert.Equal("steam:42", altTabbed.ApplicationId);
        Assert.Equal("game.exe", altTabbed.RtssProfileName);
        Assert.Equal(game.Generation, altTabbed.Generation);
    }

    [Fact]
    public void ALauncherHandsThePairingToTheGameProcessFromTheSameFolder()
    {
        // A launcher takes focus first and validly pairs; when the game process from the same
        // install folder comes to the front, the profile follows it rather than staying on the
        // launcher for the whole run.
        SteamRunningAppObservation observation = new(true, [42], 2, null);
        SteamRunningAppProfile unresolved = new(
            null,
            null,
            "Steam exposes no executable.",
            @"D:\SteamLibrary\steamapps\common\Game");
        var launcher = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            observation,
            unresolved,
            new ForegroundApplicationObservation(
                "launcher.exe",
                @"D:\SteamLibrary\steamapps\common\Game\launcher.exe"));

        var game = RunningApplicationTargetProjection.Apply(
            launcher,
            observation,
            unresolved,
            new ForegroundApplicationObservation(
                "game.exe",
                @"D:\SteamLibrary\steamapps\common\Game\bin\game.exe"));

        Assert.Equal("launcher.exe", launcher.RtssProfileName);
        Assert.Equal("game.exe", game.RtssProfileName);
        Assert.Equal("steam:42", game.ApplicationId);
    }

    [Fact]
    public void AmbiguousSteamStateIsNotBrokenByTheForegroundWindow()
    {
        // The foreground says which window has focus, not which of two running games the user
        // means; choosing one here would write a power limit against the other.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [42, 43], 2, null),
            null,
            new ForegroundApplicationObservation("game.exe"));

        Assert.Equal(RunningApplicationTargetState.Ambiguous, target.State);
        Assert.Null(target.ApplicationId);
    }

    [Fact]
    public void AnUnreachableSteamStaysUnavailableRatherThanGuessingFromFocus()
    {
        // Unavailable means the observation failed. Publishing an identity from focus would claim
        // knowledge WSGM does not have about whether a game is running.
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(false, [], 0, "Steam is unreachable."),
            null,
            new ForegroundApplicationObservation("game.exe"));

        Assert.Equal(RunningApplicationTargetState.Unavailable, target.State);
        Assert.Null(target.RtssProfileName);
    }

    [Theory]
    [InlineData("wsgm.exe")]
    [InlineData("explorer.exe")]
    [InlineData("readme.txt")]
    [InlineData("")]
    public void ForegroundWindowsThatAreNotApplicationsLeavePolicyGlobal(string executable)
    {
        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            new SteamRunningAppObservation(true, [], 3, null),
            null,
            new ForegroundApplicationObservation(executable));

        Assert.Equal(RunningApplicationTargetState.Global, target.State);
        Assert.Null(target.ApplicationId);
    }

    [Fact]
    public void ReturningToTheSameForegroundApplicationDoesNotChurnTheGeneration()
    {
        SteamRunningAppObservation idle = new(true, [], 3, null);
        ForegroundApplicationObservation foreground = new("game.exe");

        var first = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            idle,
            null,
            foreground);
        var second = RunningApplicationTargetProjection.Apply(
            first,
            idle,
            null,
            foreground);

        Assert.Equal(first.Generation, second.Generation);
    }

    [Fact]
    public async Task DeliberatelyDisabledCefStillAllowsForegroundApplicationPolicy()
    {
        await using var transport = new DisabledTransport();
        var probe = new SteamRunningApplicationProbe(transport);
        var observation = await probe.ObserveAsync(CancellationToken.None);

        var target = RunningApplicationTargetProjection.Apply(
            RunningApplicationTargetSnapshot.Initial(),
            observation,
            null,
            new ForegroundApplicationObservation("game.exe"));

        Assert.True(observation.Reachable);
        Assert.Empty(observation.AppIds);
        Assert.Equal(RunningApplicationTargetState.Active, target.State);
        Assert.Equal("game.exe", target.RtssProfileName);
    }

    private sealed class DisabledTransport : ISteamUiTransport
    {
        public event EventHandler<SteamUiNotification>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<SteamUiTransportSnapshot>? GenerationChanged
        {
            add { }
            remove { }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            SteamUiTargetRole role,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SteamUiEvaluationResult> EvaluateAsync(
            SteamUiTargetRole role,
            string expression,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SteamUiEvaluationResult.Unavailable(
                "Steam CEF integration disabled in settings.",
                default));
        }

        public Task SetRuntimeBindingAsync(
            SteamUiTargetRole role,
            string bindingName,
            bool installed,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots()
        {
            return [];
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
