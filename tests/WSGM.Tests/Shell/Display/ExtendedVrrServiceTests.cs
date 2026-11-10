using LibGPUDriverInteract;
using LibHandheld.Contracts;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

/// <summary>
///     The extended VRR range's transitions against a fake registry, standalone tool and Intel driver, following the
///     Atlas8Tweaks script's Install, Repair and Uninstall.
/// </summary>
/// <remarks>
///     The panel is the Atlas panel's facts over a synthetic physical EDID (the real one is not in the repository),
///     so the hashes here are the fixture's. Nothing in these tests reaches Windows or a driver.
/// </remarks>
public sealed class ExtendedVrrServiceTests
{
    private static readonly byte[] Physical = SyntheticPhysical();

    private static readonly ExtendedVrrPanel Panel = SyntheticPanel();

    private static readonly DisplayTargetIdentity Target =
        new(@"\\?\DISPLAY#BOE0CFF#4&1&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", null, null, "NS080WUM-LX1",
            1, 0, 1);

    [Fact]
    public async Task WithoutAPanelNothingIsReadOrWritten()
    {
        var fixture = new Fixture(hasPanel: false);

        await fixture.CheckAsync();
        var status = await fixture.Service.SetEnabledAsync(true);

        Assert.Equal(ExtendedVrrPhase.Hidden, status.Phase);
        Assert.Equal(0, fixture.Machine.Finds);
        Assert.Empty(fixture.Driver.Writes);
    }

    [Fact]
    public async Task TheFactoryPanelIsOffAndTurningItOnSavesTheMeasuredStateAndWritesTheOverride()
    {
        var fixture = new Fixture();
        await fixture.CheckAsync();
        Assert.Equal((ExtendedVrrPhase.Off, false, true),
            (fixture.Service.Status.Phase, fixture.Service.Status.Enabled, fixture.Service.Status.CanChange));

        var status = await fixture.Service.SetEnabledAsync(true);

        Assert.Equal(ExtendedVrrPhase.RestartToApply, status.Phase);
        Assert.Equal("Restart Windows to apply.", status.Detail);
        Assert.Equal(Panel.CreateEdid(Physical, 28), fixture.Machine.Override);
        Assert.Equal(new ExtendedVrrIntelState
        {
            Source = "MEASURED", ProfileId = 7, MinimumHz = 48, MaximumHz = 120, LowFpsSolutionEnabled = true,
            HighFpsSolutionEnabled = true
        }, fixture.Saved);
        Assert.Empty(fixture.Driver.Writes);
    }

    [Fact]
    public async Task AfterTheRestartEveryStartAppliesExcellentAcrossTheFloorAndTurnsLfcOff()
    {
        var fixture = new Fixture();
        fixture.Activate();

        await fixture.CheckAsync();

        Assert.Equal(ExtendedVrrPhase.Active, fixture.Service.Status.Phase);
        Assert.Equal(
        [
            "baseline Excellent 28-120", "profile Excellent 28-120", "solution LowFrameRate False"
        ], fixture.Driver.Writes);
        Assert.Equal((GpuArcSyncProfile.Excellent, 28d), (fixture.Driver.Profile.Profile, fixture.Driver.Profile.MinimumHz));
        Assert.False(fixture.Driver.Solutions.LowFrameRate);
        Assert.True(fixture.Driver.Solutions.HighFrameRate);
    }

    [Fact]
    public async Task AKeptLowFrameRateCompensationStaysOnAtEveryStart()
    {
        var fixture = new Fixture(lowFrameRate: true);
        fixture.Activate();

        await fixture.CheckAsync();

        Assert.Equal(["baseline Excellent 28-120", "profile Excellent 28-120"], fixture.Driver.Writes);
        Assert.True(fixture.Driver.Solutions.LowFrameRate);
        Assert.True(fixture.Service.Status.LowFrameRateCompensation);
    }

    [Fact]
    public async Task TheLowFrameRateSwitchIsSavedAndAppliedAtOnceWhileActive()
    {
        var fixture = new Fixture();
        fixture.Activate();
        await fixture.CheckAsync();
        fixture.Driver.Writes.Clear();

        var on = await fixture.Service.SetLowFrameRateCompensationAsync(true);
        Assert.True(fixture.LowFrameRate);
        Assert.True(on.LowFrameRateCompensation);
        Assert.True(fixture.Driver.Solutions.LowFrameRate);
        Assert.Equal(["baseline Excellent 28-120", "solution LowFrameRate True"], fixture.Driver.Writes);

        var off = await fixture.Service.SetLowFrameRateCompensationAsync(false);
        Assert.False(fixture.LowFrameRate);
        Assert.False(off.LowFrameRateCompensation);
        Assert.False(fixture.Driver.Solutions.LowFrameRate);
    }

    [Fact]
    public async Task TheLowFrameRateSwitchIsOnlyShownAndAppliedWhileTheRangeIsActive()
    {
        var fixture = new Fixture();
        await fixture.CheckAsync();
        Assert.Equal(ExtendedVrrPhase.Off, fixture.Service.Status.Phase);
        Assert.False(fixture.Service.Status.LowFrameRateVisible);

        var status = await fixture.Service.SetLowFrameRateCompensationAsync(true);

        Assert.True(status.LowFrameRateCompensation);
        Assert.True(fixture.LowFrameRate);
        Assert.Empty(fixture.Driver.Writes);
    }
    [Fact]
    public async Task AnAlreadyRepairedPanelOnlyGetsItsBaseline()
    {
        var fixture = new Fixture();
        fixture.Activate();
        await fixture.CheckAsync();
        fixture.Driver.Writes.Clear();

        await fixture.CheckAsync();

        Assert.Equal(["baseline Excellent 28-120"], fixture.Driver.Writes);
    }

    [Fact]
    public async Task AFixedRefreshRateKeepsItsHoldAndExcellentWaitsForTheRelease()
    {
        var fixture = new Fixture();
        fixture.Activate();
        fixture.Driver.FixedHz = 60;

        await fixture.CheckAsync();

        Assert.Equal(["baseline Excellent 28-120", "solution LowFrameRate False"], fixture.Driver.Writes);
        Assert.Equal(ExtendedVrrPhase.Active, fixture.Service.Status.Phase);
    }

    [Fact]
    public async Task AFloorIntelDoesNotReportChangesNothing()
    {
        var fixture = new Fixture();
        fixture.Activate();
        fixture.Driver.MonitorMinimumHz = 48;

        await fixture.CheckAsync();

        Assert.Empty(fixture.Driver.Writes);
        Assert.Contains("Nothing was changed", fixture.Service.Status.Detail);
    }

    [Fact]
    public async Task AnUnansweredExcellentWriteIsReportedAndNotSentAgain()
    {
        var fixture = new Fixture();
        fixture.Activate();
        fixture.Driver.AnswerProfileWrites = false;

        await fixture.CheckAsync();

        Assert.Equal(["baseline Excellent 28-120", "profile Excellent 28-120"], fixture.Driver.Writes);
        Assert.StartsWith("Could not apply Arc Sync Excellent", fixture.Service.Status.Detail);
    }

    [Fact]
    public async Task TurningItOffRemovesTheOverrideAndRestoresTheSavedIntelState()
    {
        var fixture = new Fixture();
        await fixture.Service.SetEnabledAsync(true);
        fixture.Restart(Panel.CreateEdid(Physical, 28));
        await fixture.CheckAsync();
        fixture.Driver.Writes.Clear();

        var status = await fixture.Service.SetEnabledAsync(false);

        Assert.Equal(ExtendedVrrPhase.RestartToRemove, status.Phase);
        Assert.Null(fixture.Machine.Override);
        Assert.Equal(["baseline none", "profile Custom 48-120", "solution LowFrameRate True"], fixture.Driver.Writes);
        Assert.Null(fixture.Saved);

        fixture.Restart(Physical);
        await fixture.CheckAsync();
        Assert.Equal(ExtendedVrrPhase.Off, fixture.Service.Status.Phase);
    }

    [Fact]
    public async Task AnUnrestoredIntelStateKeepsTheSavedRecordForTheNextTry()
    {
        var fixture = new Fixture();
        await fixture.Service.SetEnabledAsync(true);
        fixture.Restart(Panel.CreateEdid(Physical, 28));
        await fixture.CheckAsync();
        fixture.Driver.AnswerProfileWrites = false;

        var status = await fixture.Service.SetEnabledAsync(false);

        Assert.StartsWith("Could not restore everything", status.Detail);
        Assert.NotNull(fixture.Saved);
        Assert.Null(fixture.Machine.Override);
    }

    [Fact]
    public async Task TurningItOnWhileTheExtendedEdidIsStillLoadedSavesTheFactoryState()
    {
        var fixture = new Fixture();
        fixture.Machine.Reported = Panel.CreateEdid(Physical, 28);

        await fixture.CheckAsync();
        Assert.Equal(ExtendedVrrPhase.RestartToRemove, fixture.Service.Status.Phase);
        var status = await fixture.Service.SetEnabledAsync(true);

        Assert.Equal(ExtendedVrrPhase.Active, status.Phase);
        Assert.Equal("FACTORY_DEFAULT", fixture.Saved!.Source);
    }

    [Fact]
    public async Task AForeignOverrideIsRefusedAndNeverReplaced()
    {
        var fixture = new Fixture();
        var foreign = Physical.ToArray();
        foreign[20] ^= 1;
        fixture.Machine.Override = foreign;

        await fixture.CheckAsync();
        var status = await fixture.Service.SetEnabledAsync(true);

        Assert.Equal(ExtendedVrrPhase.Refused, status.Phase);
        Assert.False(status.CanChange);
        Assert.Contains("Another tool's EDID override", status.Detail);
        Assert.Same(foreign, fixture.Machine.Override);
        Assert.Null(fixture.Saved);
    }

    [Fact]
    public async Task AnOverrideOfAnotherShapeIsForeign()
    {
        var fixture = new Fixture();
        fixture.Machine.OverridePresentWithoutEdid = true;

        await fixture.CheckAsync();

        Assert.Equal(ExtendedVrrPhase.Refused, fixture.Service.Status.Phase);
    }

    [Fact]
    public async Task AnUnknownPanelEdidIsRefusedWithItsReason()
    {
        var fixture = new Fixture();
        var other = Physical.ToArray();
        other[20] ^= 1;
        fixture.Machine.Reported = other;

        await fixture.CheckAsync();

        Assert.Equal(ExtendedVrrPhase.Refused, fixture.Service.Status.Phase);
        Assert.Contains("Unsupported panel EDID", fixture.Service.Status.Detail);
    }

    [Fact]
    public async Task WithoutArcSyncTheRowIsHidden()
    {
        var fixture = new Fixture();
        fixture.Driver.HasArcSync = false;

        await fixture.CheckAsync();

        Assert.Equal(ExtendedVrrPhase.Hidden, fixture.Service.Status.Phase);
    }

    [Fact]
    public async Task ARecheckOnceTheDriverAnswersShowsTheRangeAndRepairsIt()
    {
        // The screen was off at start: Intel answered no Arc Sync until the display was lit again.
        var fixture = new Fixture();
        fixture.Activate();
        fixture.Driver.HasArcSync = false;
        await fixture.CheckAsync();
        Assert.Equal(ExtendedVrrPhase.Hidden, fixture.Service.Status.Phase);

        fixture.Driver.HasArcSync = true;
        await fixture.CheckAsync();

        Assert.Equal(ExtendedVrrPhase.Active, fixture.Service.Status.Phase);
        Assert.False(fixture.Driver.Solutions.LowFrameRate);
    }

    [Fact]
    public async Task TheTestBuildsOverrideIsRecognizedAsOurs()
    {
        var fixture = new Fixture();
        var testBuild = TestBuildVariant(28);
        fixture.Machine.Override = testBuild;
        fixture.Restart(testBuild);

        await fixture.CheckAsync();

        Assert.Equal(ExtendedVrrPhase.Active, fixture.Service.Status.Phase);
    }

    [Fact]
    public async Task AStandaloneInstallIsShownAsActiveAndLeftToItsOwnTask()
    {
        var fixture = new Fixture();
        fixture.Machine.Override = Panel.CreateEdid(Physical, 28);
        fixture.Restart(Panel.CreateEdid(Physical, 28));
        fixture.Machine.Standalone = new ExtendedVrrStandalone(28, null);

        await fixture.CheckAsync();

        Assert.Equal((ExtendedVrrPhase.Standalone, true), (fixture.Service.Status.Phase, fixture.Service.Status.Enabled));
        Assert.Contains("standalone Atlas8Tweaks", fixture.Service.Status.Detail);
        Assert.Equal(["baseline Excellent 28-120"], fixture.Driver.Writes);
        Assert.False(fixture.Machine.StandaloneRemoved);
    }

    [Fact]
    public async Task TheLowFrameRateSwitchTakesALoadedStandaloneRangeOverWithoutARestart()
    {
        var fixture = new Fixture();
        fixture.Machine.Override = Panel.CreateEdid(Physical, 28);
        fixture.Restart(Panel.CreateEdid(Physical, 28));
        var original = new ExtendedVrrIntelState
        {
            Source = "MEASURED", ProfileId = 1, MinimumHz = 48, MaximumHz = 120, LowFpsSolutionEnabled = true,
            HighFpsSolutionEnabled = true
        };
        fixture.Machine.Standalone = new ExtendedVrrStandalone(28, original);
        await fixture.CheckAsync();
        Assert.True(fixture.Service.Status.LowFrameRateVisible);

        var status = await fixture.Service.SetLowFrameRateCompensationAsync(true);

        Assert.True(fixture.Machine.StandaloneRemoved);
        Assert.NotNull(fixture.Machine.Override);
        Assert.Equal(original, fixture.Saved);
        Assert.Equal((ExtendedVrrPhase.Active, true, true),
            (status.Phase, status.LowFrameRateCompensation, status.LowFrameRateVisible));
        Assert.True(fixture.Driver.Solutions.LowFrameRate);
    }

    [Fact]
    public async Task TheLowFrameRateSwitchIsHiddenUntilTheRangeIsLoaded()
    {
        var fixture = new Fixture();
        await fixture.Service.SetEnabledAsync(true);

        Assert.Equal(ExtendedVrrPhase.RestartToApply, fixture.Service.Status.Phase);
        Assert.False(fixture.Service.Status.LowFrameRateVisible);
    }
    [Fact]
    public async Task TurningAStandaloneInstallOffRemovesItAndRestoresItsSavedState()
    {
        var fixture = new Fixture();
        fixture.Machine.Override = Panel.CreateEdid(Physical, 28);
        fixture.Restart(Panel.CreateEdid(Physical, 28));
        fixture.Machine.Standalone = new ExtendedVrrStandalone(28, new ExtendedVrrIntelState
        {
            Source = "MEASURED", ProfileId = 1, MinimumHz = 48, MaximumHz = 120, LowFpsSolutionEnabled = true,
            HighFpsSolutionEnabled = true
        });
        await fixture.CheckAsync();
        fixture.Driver.Writes.Clear();

        var status = await fixture.Service.SetEnabledAsync(false);

        Assert.True(fixture.Machine.StandaloneRemoved);
        Assert.Null(fixture.Machine.Override);
        Assert.Contains("profile Recommended 48-120", fixture.Driver.Writes);
        Assert.Contains("removed the standalone", status.Detail);
        Assert.Null(fixture.Saved);
    }

    [Fact]
    public async Task UninstallRestoresOnlyWhatWsgmTurnedOn()
    {
        var untouched = new Fixture();
        untouched.Machine.Override = Panel.CreateEdid(Physical, 28);
        Assert.Equal((true, false), await untouched.Service.RestoreForUninstallAsync());
        Assert.NotNull(untouched.Machine.Override);

        var fixture = new Fixture();
        await fixture.Service.SetEnabledAsync(true);
        fixture.Restart(Panel.CreateEdid(Physical, 28));

        Assert.Equal((true, true), await fixture.Service.RestoreForUninstallAsync());
        Assert.Null(fixture.Machine.Override);
        Assert.Null(fixture.Saved);
    }

    private static byte[] TestBuildVariant(int floor)
    {
        var edid = Physical.ToArray();
        foreach (var offset in Panel.TestBuildFloorOffsets)
        {
            edid[offset] = (byte)floor;
        }

        Checksums(edid);
        return edid;
    }

    private static byte[] SyntheticPhysical()
    {
        var edid = new byte[256];
        for (var index = 0; index < edid.Length; index++)
        {
            edid[index] = (byte)((index * 37 + 11) % 251);
        }

        byte[] header = [0, 255, 255, 255, 255, 255, 255, 0];
        header.CopyTo(edid, 0);
        // BOE, product 0CFF, monitor name NS080WUM-LX1.
        edid[8] = 0x09;
        edid[9] = 0xE5;
        edid[10] = 0xFF;
        edid[11] = 0x0C;
        byte[] name = [0, 0, 0, 0xFC, 0, .. "NS080WUM-LX1\n "u8];
        name.CopyTo(edid, 54);
        foreach (var offset in (int[]) [95, 142, 154, 174, 180])
        {
            edid[offset] = 48;
        }

        Checksums(edid);
        return edid;
    }

    private static ExtendedVrrPanel SyntheticPanel()
    {
        return new ExtendedVrrPanel("BOE", "0CFF", "NS080WUM-LX1", ExtendedVrrPanel.Sha256(Physical), 48, 120, 28,
            [95, 142, 154, 174, 180], [95, 142, 154, 174], new ExtendedVrrFactoryState(7, 48, 120, 0, 0, true, true));
    }

    private static void Checksums(byte[] edid)
    {
        var sum = 0;
        for (var index = 129; index < 254; index++)
        {
            sum += edid[index];
        }

        edid[254] = (byte)((256 - sum % 256) % 256);
        foreach (var start in (int[]) [0, 128])
        {
            sum = 0;
            for (var index = start; index < start + 127; index++)
            {
                sum += edid[index];
            }

            edid[start + 127] = (byte)((256 - sum % 256) % 256);
        }
    }

    private sealed class Fixture
    {
        public Fixture(bool hasPanel = true, bool lowFrameRate = false)
        {
            var panel = hasPanel ? Panel : null;
            LowFrameRate = lowFrameRate;
            Service = new ExtendedVrrService(() => panel, Machine, Driver, () => Saved, saved => Saved = saved,
                TimeSpan.Zero, (_, _) => Task.CompletedTask, () => LowFrameRate, enabled => LowFrameRate = enabled);
        }

        /// <summary>The saved low frame rate compensation choice.</summary>
        public bool LowFrameRate { get; private set; }

        public FakeMachine Machine { get; } = new();

        public FakeDriver Driver { get; } = new();

        public ExtendedVrrIntelState? Saved { get; private set; }

        public ExtendedVrrService Service { get; }

        public Task CheckAsync()
        {
            Service.Recheck();
            return Service.WaitForCheckAsync();
        }

        /// <summary>Our 28 Hz override, written and loaded by a restart.</summary>
        public void Activate()
        {
            Machine.Override = Panel.CreateEdid(Physical, 28);
            Restart(Machine.Override);
        }

        /// <summary>A Windows restart: the written override, or the given EDID, becomes the reported one.</summary>
        public void Restart(byte[] reported)
        {
            Machine.Reported = Machine.Override ?? reported;
            Driver.MonitorMinimumHz = Panel.Read(Machine.Reported).Reported?.FloorHz ?? 48;
        }
    }

    private sealed class FakeMachine : IExtendedVrrMachine
    {
        public byte[] Reported { get; set; } = Physical;

        public byte[]? Override { get; set; }

        public bool OverridePresentWithoutEdid { get; set; }

        public ExtendedVrrStandalone? Standalone { get; set; }

        public bool StandaloneRemoved { get; private set; }

        public int Finds { get; private set; }

        public IReadOnlyList<ExtendedVrrDisplay> FindPanels(ExtendedVrrPanel panel)
        {
            Finds++;
            return panel.Identifies(Reported) ? [new ExtendedVrrDisplay(Target, Reported)] : [];
        }

        public DisplayEdidOverride ReadOverride(DisplayTargetIdentity target)
        {
            return OverridePresentWithoutEdid
                ? new DisplayEdidOverride(true, null)
                : Override is null
                    ? DisplayEdidOverride.None
                    : new DisplayEdidOverride(true, Override);
        }

        public void WriteOverride(DisplayTargetIdentity target, byte[] edid)
        {
            Override = edid;
        }

        public bool RemoveOverride(DisplayTargetIdentity target)
        {
            var removed = Override is not null;
            Override = null;
            return removed;
        }

        public ExtendedVrrStandalone? ReadStandalone()
        {
            return Standalone;
        }

        public string? RemoveStandalone()
        {
            StandaloneRemoved = true;
            Standalone = null;
            return null;
        }
    }

    private sealed class FakeDriver : IExtendedVrrDriver
    {
        public List<string> Writes { get; } = [];

        public bool HasArcSync { get; set; } = true;

        public bool AnswerProfileWrites { get; set; } = true;

        public double MonitorMinimumHz { get; set; } = 48;

        public double? FixedHz { get; set; }

        public GpuArcSyncBaseline Profile { get; private set; } = new(GpuArcSyncProfile.Custom, 48, 120);

        public GpuVrrSolutions Solutions { get; private set; } = new(true, true, true, true);

        public Task<GpuArcSyncState?> ReadArcSyncAsync(DisplayTargetIdentity target)
        {
            return Task.FromResult(HasArcSync
                ? new GpuArcSyncState(new GpuDisplayTarget(), MonitorMinimumHz, 120,
                    FixedHz is { } held ? new GpuArcSyncBaseline(GpuArcSyncProfile.Custom, held, held) : Profile,
                    FixedHz, FixedHz is null ? null : Profile)
                : null);
        }

        public Task<bool> SetBaselineAsync(DisplayTargetIdentity target, GpuArcSyncBaseline? baseline)
        {
            Writes.Add(baseline is null ? "baseline none" : $"baseline {Text(baseline)}");
            return Task.FromResult(true);
        }

        public Task<GpuDisplayWriteResult?> SetProfileAsync(DisplayTargetIdentity target, GpuArcSyncBaseline profile)
        {
            Writes.Add($"profile {Text(profile)}");
            if (!AnswerProfileWrites)
            {
                return Task.FromResult<GpuDisplayWriteResult?>(null);
            }

            Profile = profile;
            return Task.FromResult<GpuDisplayWriteResult?>(new GpuDisplayWriteResult(true, "accepted"));
        }

        public Task<GpuVrrSolutions?> ReadSolutionsAsync(DisplayTargetIdentity target)
        {
            return Task.FromResult<GpuVrrSolutions?>(Solutions);
        }

        public Task<GpuDisplayWriteResult?> SetSolutionAsync(DisplayTargetIdentity target, GpuVrrSolution solution,
            bool enabled)
        {
            Writes.Add($"solution {solution} {enabled}");
            Solutions = solution is GpuVrrSolution.LowFrameRate
                ? Solutions with { LowFrameRate = enabled }
                : Solutions with { HighFrameRate = enabled };
            return Task.FromResult<GpuDisplayWriteResult?>(new GpuDisplayWriteResult(true, "accepted"));
        }

        private static string Text(GpuArcSyncBaseline profile)
        {
            return $"{profile.Profile} {profile.MinimumHz:0}-{profile.MaximumHz:0}";
        }
    }
}
