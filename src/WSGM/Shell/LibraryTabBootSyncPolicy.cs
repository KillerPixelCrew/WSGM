namespace WSGM.Shell;

/// <summary>Next step after one automatic library-tab synchronization attempt.</summary>
internal enum LibraryTabBootAction
{
    RetryFullSync,
    RetryBadges,
    Complete,
}

/// <summary>Keeps boot retries tied to the part that actually succeeded.</summary>
internal static class LibraryTabBootSyncPolicy
{
    /// <summary>Returns whether to retry tabs, retry only badges, or finish.</summary>
    internal static LibraryTabBootAction Decide(LibraryTabSyncResult result)
    {
        if (!result.Success)
        {
            return LibraryTabBootAction.RetryFullSync;
        }
        return result.BadgesPushed
            ? LibraryTabBootAction.Complete
            : LibraryTabBootAction.RetryBadges;
    }
}
