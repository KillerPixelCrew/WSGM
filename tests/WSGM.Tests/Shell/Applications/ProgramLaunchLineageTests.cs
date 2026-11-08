using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests.Shell.Applications;

public sealed class ProgramLaunchLineageTests
{
    private static readonly DateTime Dispatched = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private const string Launcher = @"C:\Tools\Bootstrap.exe";

    [Fact]
    public void AConfiguredJunctionAndItsKernelPhysicalImageIdentifyTheSameLaunch()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched, @"C:\Tools\Version\Bootstrap.exe");
        lineage.ObserveStart(new ProcessStartIdentity(
            new ProcessImageIdentity(10, Dispatched, @"C:\Tools\Version\Bootstrap.exe"), 100, 1));
        ProcessImageIdentity ui = new(12, Dispatched.AddMilliseconds(1), @"C:\Tools\Version\Ui.exe");
        lineage.ObserveStart(new ProcessStartIdentity(ui, 101, 100));

        Assert.True(lineage.Owns(ui with { Image = @"C:\Tools\Ui.exe" }));
    }

    [Fact]
    public void KernelCreationSequencesResolveAnEntireAlreadyExitedBootstrapChain()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched);
        ProcessImageIdentity root = new(10, Dispatched.AddMilliseconds(1), Launcher);
        ProcessImageIdentity helper = new(11, Dispatched.AddMilliseconds(2), @"C:\Tools\Helper.exe");
        ProcessImageIdentity ui = new(12, Dispatched.AddMilliseconds(3), @"C:\Tools\ActualUi.exe");
        // ETW buffers from different processors can arrive out of order.
        lineage.ObserveStart(new ProcessStartIdentity(ui, 102, 101));
        lineage.ObserveStart(new ProcessStartIdentity(helper, 101, 100));
        lineage.ObserveStart(new ProcessStartIdentity(root, 100, 1));

        Assert.True(lineage.Owns(ui));
    }

    [Fact]
    public void AReusedParentPidWithAnotherKernelSequenceDoesNotOwnItsChild()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched);
        lineage.ObserveStart(new ProcessStartIdentity(new ProcessImageIdentity(10, Dispatched, Launcher), 100, 1));
        ProcessImageIdentity ui = new(12, Dispatched.AddMilliseconds(3), @"C:\Other\ActualUi.exe");
        lineage.ObserveStart(new ProcessStartIdentity(ui, 102, 101));

        Assert.False(lineage.Owns(ui));
    }

    [Fact]
    public void DifferentlyNamedChildIsOwnedAfterItsVerifiedBootstrapExits()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched);
        ProcessImageIdentity root = new(10, Dispatched.AddMilliseconds(1), Launcher);
        ProcessImageIdentity child = new(11, Dispatched.AddMilliseconds(2), @"C:\Tools\App\ActualUi.exe");
        lineage.Observe([new ProcessSnapshotEntry(10, 1, "Bootstrap.exe")], id => id == 10 ? root : null);

        // The observation retains the exited parent's process object, so its id cannot be reused.
        lineage.Observe([new ProcessSnapshotEntry(11, 10, "ActualUi.exe")], id => id == 11 ? child : root);

        Assert.True(lineage.Owns(child));
    }

    [Fact]
    public void FilenameAndNearbyCreationTimeDoNotAuthorizeAnUnrelatedImage()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched);
        ProcessImageIdentity unrelated = new(10, Dispatched.AddMilliseconds(1), @"C:\Other\Bootstrap.exe");
        lineage.Observe([new ProcessSnapshotEntry(10, 1, "Bootstrap.exe")], _ => unrelated);

        Assert.False(lineage.Owns(unrelated));
    }

    [Fact]
    public void PreexistingBootstrapAndChildrenAreNotAssignedToANewDispatch()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched);
        ProcessImageIdentity root = new(10, Dispatched.AddSeconds(-1), Launcher);
        ProcessImageIdentity child = new(11, Dispatched.AddMilliseconds(2), @"C:\Tools\ActualUi.exe");
        lineage.Observe([new ProcessSnapshotEntry(10, 1, "Bootstrap.exe"),
            new ProcessSnapshotEntry(11, 10, "ActualUi.exe")], id => id == 10 ? root : child);

        Assert.False(lineage.Owns(root));
        Assert.False(lineage.Owns(child));
    }

    [Fact]
    public void ChildBeforeParentInTheSnapshotStillResolvesAndReusedIdsDoNotMatch()
    {
        ProgramLaunchLineage lineage = new(Launcher, Dispatched);
        ProcessImageIdentity root = new(10, Dispatched.AddMilliseconds(1), Launcher);
        ProcessImageIdentity child = new(11, Dispatched.AddMilliseconds(2), @"C:\Tools\ActualUi.exe");
        lineage.Observe([new ProcessSnapshotEntry(11, 10, "ActualUi.exe"),
            new ProcessSnapshotEntry(10, 1, "Bootstrap.exe")], id => id == 10 ? root : child);

        Assert.True(lineage.Owns(child));
        Assert.False(lineage.Owns(child with { Started = child.Started.AddSeconds(1) }));
    }
}
