using WSGM.Device.Tests;
using WSGM.PackagedLaunch;

namespace WSGM.Tests.PackagedLaunch;

/// <summary>
///     The journal that stops a killed launcher leaving a package permanently exempt from Process
///     Lifetime Management. Windows keeps a package out of PLM until something puts it back, so a
///     record that is never replayed means a game that is never suspended again for the rest of the
///     machine's life.
/// </summary>
public sealed class PackageDebugRecoveryRecordTests
{
    private const string Package = "Publisher.Game_1.0.0.0_x64__abc123";

    private static PackageDebugRecoveryRecord Journal(
        string path, Func<int, DateTime?, bool> isOwnerAlive)
    {
        return new PackageDebugRecoveryRecord(path, isOwnerAlive);
    }

    [Fact]
    public void ARecordWhoseOwnerIsGoneIsReplayed()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 4242, DateTime.UtcNow);

        var abandoned = Journal(path, static (_, _) => false).ListAbandoned();

        Assert.Equal(Package, Assert.Single(abandoned));
    }

    [Fact]
    public void ARecordOwnedByALiveLauncherIsLeftAlone()
    {
        // Releasing a live launcher's exemption would suspend the game it is supervising.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 4242, DateTime.UtcNow);

        Assert.Empty(Journal(path, static (_, _) => true).ListAbandoned());
    }

    [Fact]
    public void ARecordSurvivesUntilItsPackageIsActuallyReleased()
    {
        // The record is the only thing that could put the package back, so a sweep that listed it
        // and then failed to release it must still find it next time.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 4242, DateTime.UtcNow);
        var journal = Journal(path, static (_, _) => false);

        Assert.Single(journal.ListAbandoned());
        Assert.Single(journal.ListAbandoned());

        journal.Forget(Package);

        Assert.Empty(journal.ListAbandoned());
    }

    [Fact]
    public void APackageThatKeepsRefusingReleaseIsNeverDropped()
    {
        // The record is the only handle that can ever put the package back, so no number of
        // failures is a reason to throw it away.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 4242, DateTime.UtcNow);
        var journal = Journal(path, static (_, _) => false);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.Single(journal.ListAbandoned());
        }
    }

    [Fact]
    public void APackageALiveLauncherStillOwnsIsNotReleased()
    {
        // Releasing is package-wide: releasing a dead launcher's record would take the exemption
        // away from the game another launcher is running right now.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var writer = Journal(path, static (_, _) => true);
        writer.Add(Package, 1111, DateTime.UtcNow);
        writer.Add(Package, 2222, DateTime.UtcNow);

        var journal = Journal(path, static (pid, _) => pid == 2222);

        Assert.Empty(journal.ListAbandoned());
    }

    [Fact]
    public void ANormalReleaseLeavesNothingToReplay()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var journal = Journal(path, static (_, _) => true);
        journal.Add(Package, 4242, DateTime.UtcNow);
        journal.Remove(Package, 4242);

        Assert.Empty(Journal(path, static (_, _) => false).ListAbandoned());
    }

    [Fact]
    public void AnotherLaunchersRecordSurvivesThisOnesRelease()
    {
        // Two packaged games can run at once, and one exiting must not drop the other's record.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var journal = Journal(path, static (_, _) => true);
        journal.Add(Package, 4242, DateTime.UtcNow);
        journal.Add("Other.Game_1.0.0.0_x64__xyz789", 5353, DateTime.UtcNow);
        journal.Remove(Package, 4242);

        var abandoned = Journal(path, static (_, _) => false).ListAbandoned();

        Assert.Equal("Other.Game_1.0.0.0_x64__xyz789", Assert.Single(abandoned));
    }

    [Fact]
    public void TheOwnerCheckSeesTheStartTimeThatWasRecorded()
    {
        // A process id alone proves nothing: Windows reuses them, so the check is given the start
        // time to compare against and must actually receive it.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var started = new DateTime(2026, 9, 22, 10, 30, 0, DateTimeKind.Utc);
        Journal(path, static (_, _) => true).Add(Package, 4242, started);

        DateTime? observed = null;
        Journal(path, (_, startedUtc) =>
        {
            observed = startedUtc;
            return true;
        }).ListAbandoned();

        Assert.Equal(started, observed);
    }

    [Fact]
    public void AnEmptyJournalIsRemovedRatherThanLeftBehind()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var journal = Journal(path, static (_, _) => true);
        journal.Add(Package, 4242, DateTime.UtcNow);
        Assert.True(File.Exists(path));

        journal.Remove(Package, 4242);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AMissingJournalIsNotAFailure()
    {
        using TemporaryDirectory temporary = new();

        Assert.Empty(Journal(temporary.GetPath("absent.json"), static (_, _) => false).ListAbandoned());
    }

    [Fact]
    public void ACorruptJournalIsTreatedAsEmptyRatherThanStoppingALaunch()
    {
        // A journal that cannot be read must not stop a game starting: the exemption it would have
        // recorded is released by this launcher's own exit in every case except a kill.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(Journal(path, static (_, _) => false).ListAbandoned());
    }

    [Fact]
    public void AMalformedRecordIsDroppedRatherThanReplayed()
    {
        // An empty package name would be handed straight to DisableDebugging.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        File.WriteAllText(path, """
                                {
                                  "Records": [
                                    { "PackageFullName": "", "LauncherProcessId": 1 },
                                    { "PackageFullName": "Good_x__y", "LauncherProcessId": 0 },
                                    { "PackageFullName": "Kept_x__y", "LauncherProcessId": 7 }
                                  ]
                                }
                                """);

        var abandoned = Journal(path, static (_, _) => false).ListAbandoned();

        Assert.Equal("Kept_x__y", Assert.Single(abandoned));
    }

    [Fact]
    public void AnotherRunningLauncherIsSeenAsAnOwnerAndThisOneIsNot()
    {
        // A normal exit asks this before releasing a package-wide exemption.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var writer = Journal(path, static (_, _) => true);
        writer.Add(Package, 1111, DateTime.UtcNow);

        Assert.False(Journal(path, static (_, _) => true).HasOtherLiveOwner(Package, 1111));

        writer.Add(Package, 2222, DateTime.UtcNow);
        Assert.True(Journal(path, static (_, _) => true).HasOtherLiveOwner(Package, 1111));
        Assert.False(Journal(path, static (pid, _) => pid == 1111).HasOtherLiveOwner(Package, 1111));
    }
}
