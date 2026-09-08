using WSGM.Core;
using WindowsDeviceControl;
namespace WSGM.Tests;

public sealed class PowerRequestListTests
{
    // ---- WakeLockStatus: state + holder summary ----

    private static PowerRequestEntry Entry(
        bool display = false, bool system = false, bool away = false,
        string name = @"\Device\HarddiskVolume4\x\steam.exe", uint? pid = 10)
        => new(display, system, away, pid is null ? 0u : 1u, name, pid, null);

    [Fact]
    public void NullEntriesAreUnknown()
        => Assert.Equal((WakeLockState.Unknown, ""), WakeLockStatus.Compute(null, 1));

    [Fact]
    public void NoLocksAreFree()
        => Assert.Equal(
            (WakeLockState.Free, ""),
            WakeLockStatus.Compute([Entry()], 1));

    [Fact]
    public void DisplayLockWinsOverSystemLock()
    {
        var (state, summary) = WakeLockStatus.Compute(
            [Entry(system: true, name: @"C:\a\other.exe", pid: 11), Entry(display: true)], 1);

        Assert.Equal(WakeLockState.DisplayHeld, state);
        Assert.Equal("Screen held on by steam.exe", summary);
    }

    [Fact]
    public void AwayModeCountsAsAStandbyLock()
    {
        var (state, _) = WakeLockStatus.Compute([Entry(away: true)], 1);

        Assert.Equal(WakeLockState.SystemHeld, state);
    }

    [Fact]
    public void OwnRequestsColorTheStateButStayOutOfTheSummary()
    {
        var (state, summary) = WakeLockStatus.Compute([Entry(system: true, pid: 42)], 42);

        Assert.Equal(WakeLockState.SystemHeld, state);
        Assert.Equal("", summary);
    }

    [Fact]
    public void DuplicateHoldersCollapseWithACount()
    {
        var (_, summary) = WakeLockStatus.Compute(
            [Entry(system: true), Entry(system: true), Entry(system: true, name: @"C:\b\game.exe", pid: 11)], 1);

        Assert.Equal("Standby blocked by steam.exe ×2, game.exe", summary);
    }

    [Fact]
    public void ManyHoldersAreCappedWithAMoreSuffix()
    {
        var (_, summary) = WakeLockStatus.Compute(
            [
                Entry(system: true, name: "a.exe", pid: 1),
                Entry(system: true, name: "b.exe", pid: 2),
                Entry(system: true, name: "c.exe", pid: 3),
                Entry(system: true, name: "d.exe", pid: 4),
                Entry(system: true, name: "e.exe", pid: 5),
            ], 99);

        Assert.Equal("Standby blocked by a.exe, b.exe, c.exe +2 more", summary);
    }

    [Fact]
    public void KernelRequestersWithoutANameAreLabeled()
        => Assert.Equal("(kernel)", WakeLockStatus.HolderName(
            new PowerRequestEntry(false, true, false, 0, "", null, null)));

    // ---- WakeLockHolders: the grouped list behind the Power tab's button ----

    [Fact]
    public void UnknownSnapshotProducesNoGroupsRatherThanAnEmptyAllClear()
        => Assert.Empty(WakeLockHolders.Build(null));

    [Fact]
    public void NoLocksProduceNoGroups()
        => Assert.Empty(WakeLockHolders.Build([Entry()]));

    [Fact]
    public void HoldersAreGroupedByLockKind()
    {
        var groups = WakeLockHolders.Build(
            [Entry(display: true), Entry(system: true, name: @"C:\a\game.exe", pid: 11)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Screen kept on", groups[0].Title);
        Assert.Equal("steam.exe", Assert.Single(groups[0].Holders).Label);
        Assert.Equal("Standby blocked", groups[1].Title);
        Assert.Equal("game.exe", Assert.Single(groups[1].Holders).Label);
    }

    [Fact]
    public void IdenticalRequestsCollapseIntoOneRowWithACount()
    {
        var entries = Enumerable.Range(0, 30).Select(_ => Entry(system: true)).ToList();

        var holder = Assert.Single(Assert.Single(WakeLockHolders.Build(entries)).Holders);

        Assert.Equal("steam.exe", holder.Label);
        Assert.Equal(30, holder.Count);
    }

    [Fact]
    public void DifferentReasonsStayOnSeparateRows()
    {
        var holders = Assert.Single(WakeLockHolders.Build(
            [
                new PowerRequestEntry(false, true, false, 1, @"C:\a\steam.exe", 10, "Downloading"),
                new PowerRequestEntry(false, true, false, 1, @"C:\a\steam.exe", 10, "Streaming"),
            ])).Holders;

        Assert.Equal(2, holders.Count);
        Assert.All(holders, h => Assert.Equal(1, h.Count));
    }

    [Fact]
    public void HoldersAreSortedByCountThenName()
    {
        var holders = Assert.Single(WakeLockHolders.Build(
            [
                Entry(system: true, name: "zebra.exe", pid: 1),
                Entry(system: true, name: "alpha.exe", pid: 2),
                Entry(system: true, name: "many.exe", pid: 3),
                Entry(system: true, name: "many.exe", pid: 3),
            ])).Holders;

        Assert.Equal(["many.exe", "alpha.exe", "zebra.exe"], holders.Select(h => h.Label));
    }

    [Fact]
    public void WsgmsOwnRequestIsListedUnlikeInTheSummaryLine()
    {
        // Compute() hides it because the row above already explains WSGM's hold; the
        // full list is answering "what is holding this awake" and must not lie.
        var holder = Assert.Single(Assert.Single(
            WakeLockHolders.Build([Entry(system: true, name: @"C:\a\WSGM.exe", pid: 42)])).Holders);

        Assert.Equal("WSGM.exe", holder.Label);
    }

    [Theory]
    [InlineData(1, @"C:\a\steam.exe", "Process (pid 10): C:\\a\\steam.exe")]
    [InlineData(2, @"C:\a\svc.exe", "Service (pid 10): C:\\a\\svc.exe")]
    public void DetailNamesTheCallerKindAndPid(uint callerType, string name, string expected)
        => Assert.Equal(expected, WakeLockHolders.Describe(
            new PowerRequestEntry(false, true, false, callerType, name, 10, null)));

    [Fact]
    public void KernelCallersAreDescribedAsDriversWithoutAPid()
    {
        Assert.Equal("Driver: usbaudio", WakeLockHolders.Describe(
            new PowerRequestEntry(false, true, false, 0, "usbaudio", null, null)));
        Assert.Equal("Kernel driver", WakeLockHolders.Describe(
            new PowerRequestEntry(false, true, false, 0, "", null, null)));
    }
}
