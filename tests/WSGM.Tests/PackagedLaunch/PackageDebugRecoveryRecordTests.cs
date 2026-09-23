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

    /// <summary>What a sweep would release, with every release failing so nothing is dropped.</summary>
    private static List<string> Abandoned(PackageDebugRecoveryRecord journal)
    {
        List<string> offered = [];
        journal.ReleaseAbandoned(package =>
        {
            offered.Add(package);
            return false;
        });
        return offered;
    }

    [Fact]
    public void ARecordWhoseOwnerIsGoneIsReplayed()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 4242, DateTime.UtcNow);

        var abandoned = Abandoned(Journal(path, static (_, _) => false));

        Assert.Equal(Package, Assert.Single(abandoned));
    }

    [Fact]
    public void ARecordOwnedByALiveLauncherIsLeftAlone()
    {
        // Releasing a live launcher's exemption would suspend the game it is supervising.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 4242, DateTime.UtcNow);

        Assert.Empty(Abandoned(Journal(path, static (_, _) => true)));
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

        Assert.Single(Abandoned(journal));
        Assert.Single(Abandoned(journal));

        Assert.Equal(1, journal.ReleaseAbandoned(static _ => true));

        Assert.Empty(Abandoned(journal));
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
            Assert.Single(Abandoned(journal));
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

        Assert.Empty(Abandoned(journal));
    }

    [Fact]
    public void ANormalReleaseLeavesNothingToReplay()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var journal = Journal(path, static (_, _) => true);
        journal.Add(Package, 4242, DateTime.UtcNow);
        journal.Remove(Package, 4242);

        Assert.Empty(Abandoned(Journal(path, static (_, _) => false)));
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

        var abandoned = Abandoned(Journal(path, static (_, _) => false));

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
        Abandoned(Journal(path, (_, startedUtc) =>
        {
            observed = startedUtc;
            return true;
        }));

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

        Assert.Empty(Abandoned(Journal(temporary.GetPath("absent.json"), static (_, _) => false)));
    }

    [Fact]
    public void ACorruptJournalIsTreatedAsEmptyRatherThanStoppingALaunch()
    {
        // A journal that cannot be read must not stop a game starting: the exemption it would have
        // recorded is released by this launcher's own exit in every case except a kill.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(Abandoned(Journal(path, static (_, _) => false)));
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

        var abandoned = Abandoned(Journal(path, static (_, _) => false));

        Assert.Equal("Kept_x__y", Assert.Single(abandoned));
    }

    [Fact]
    public void TheLastLauncherOutReleasesThePackageAndEveryRecordForIt()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var writer = Journal(path, static (_, _) => true);
        writer.Add(Package, 1111, DateTime.UtcNow);
        var released = 0;

        var outcome = Journal(path, static (_, _) => true).Retire(Package, 1111, _ =>
        {
            released++;
            return true;
        });

        Assert.Equal(PackageRetirement.Released, outcome);
        Assert.Equal(1, released);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ALauncherLeavingWhileAnotherRunsThePackageReleasesNothing()
    {
        // Releasing is package-wide: the other launcher's game would be suspended on its next
        // Alt-Tab. This one drops its own record and nothing else.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var writer = Journal(path, static (_, _) => true);
        writer.Add(Package, 1111, DateTime.UtcNow);
        writer.Add(Package, 2222, DateTime.UtcNow);

        var outcome = Journal(path, static (_, _) => true).Retire(Package, 1111, static _ =>
            throw new InvalidOperationException("Must not release a package another launcher runs."));

        Assert.Equal(PackageRetirement.KeptForAnotherLauncher, outcome);
        Assert.Equal(PackageRetirement.Released,
            Journal(path, static (pid, _) => pid == 2222).Retire(Package, 2222, static _ => true));
    }

    [Fact]
    public void TwoLaunchersLeavingTogetherStillReleaseThePackageOnce()
    {
        // Checked and released in separate steps, both could see the other alive and leave the
        // package exempt with no record. Under one lock the second sees itself as the last.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var writer = Journal(path, static (_, _) => true);
        writer.Add(Package, 1111, DateTime.UtcNow);
        writer.Add(Package, 2222, DateTime.UtcNow);
        var releases = 0;
        var alive = new HashSet<int> { 1111, 2222 };
        var journal = Journal(path, (pid, _) => alive.Contains(pid));

        Parallel.ForEach(new[] { 1111, 2222 }, pid =>
        {
            journal.Retire(Package, pid, _ =>
            {
                Interlocked.Increment(ref releases);
                return true;
            });
        });

        Assert.Equal(1, releases);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFailedReleaseKeepsTheRecordForTheNextSweep()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Journal(path, static (_, _) => true).Add(Package, 1111, DateTime.UtcNow);

        Assert.Equal(PackageRetirement.Failed,
            Journal(path, static (_, _) => true).Retire(Package, 1111, static _ => false));

        Assert.Equal(Package, Assert.Single(Abandoned(Journal(path, static (_, _) => false))));
    }

    [Fact]
    public void AFullJournalRefusesANewRecordRatherThanWritingOneNothingReads()
    {
        // The reader keeps the first 64. A 65th written anyway would enable an exemption no sweep
        // could ever find again.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        var journal = Journal(path, static (_, _) => true);
        for (var pid = 1; pid <= 64; pid++)
        {
            Assert.True(journal.Add($"Game{pid}_x__y", pid, DateTime.UtcNow));
        }

        Assert.False(journal.Add("OneTooMany_x__y", 65, DateTime.UtcNow));

        // Replacing a launcher's own record is not growth, and still allowed.
        Assert.True(journal.Add("Game1_x__y", 1, DateTime.UtcNow));
    }

    [Fact]
    public void TheJournalIsSettledOnlyWhenNothingIsLeft()
    {
        // What uninstall asks before deleting the journal and the launcher.
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("recovery.json");
        Assert.True(Journal(path, static (_, _) => false).IsSettled());

        Journal(path, static (_, _) => true).Add(Package, 1111, DateTime.UtcNow);
        var journal = Journal(path, static (_, _) => false);
        journal.ReleaseAbandoned(static _ => false);
        Assert.False(journal.IsSettled());

        journal.ReleaseAbandoned(static _ => true);
        Assert.True(journal.IsSettled());
    }
}
