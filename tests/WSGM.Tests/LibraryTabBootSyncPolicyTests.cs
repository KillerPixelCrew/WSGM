using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Boot tab synchronization retries the part that was not actually
/// installed. A successful badge push cannot turn a failed tab-filter evaluation
/// into a successful tab sync.</summary>
public class LibraryTabBootSyncPolicyTests
{
    [Fact]
    public void Decide_AFailedTabSyncWithBadgesPushed_RetriesFullSync()
    {
        var result = new LibraryTabSyncResult(
            "Filter not ready.", Success: false, Reachable: true, BadgesPushed: true);

        Assert.Equal(LibraryTabBootAction.RetryFullSync, LibraryTabBootSyncPolicy.Decide(result));
    }

    [Fact]
    public void Decide_ASuccessfulTabSyncWithoutBadges_RetriesOnlyBadges()
    {
        var result = new LibraryTabSyncResult(
            "Tabs installed.", Success: true, Reachable: true, BadgesPushed: false);

        Assert.Equal(LibraryTabBootAction.RetryBadges, LibraryTabBootSyncPolicy.Decide(result));
    }

    [Fact]
    public void Decide_ASuccessfulTabAndBadgeSync_Completes()
    {
        var result = new LibraryTabSyncResult(
            "Everything installed.", Success: true, Reachable: true, BadgesPushed: true);

        Assert.Equal(LibraryTabBootAction.Complete, LibraryTabBootSyncPolicy.Decide(result));
    }
}
