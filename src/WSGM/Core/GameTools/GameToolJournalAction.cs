namespace WSGM.Core;

/// <summary>The change one journal entry made to a game file.</summary>
internal enum GameToolJournalAction
{
    /// <summary>The file did not exist and was created.</summary>
    Create,

    /// <summary>An existing file was backed up and overwritten.</summary>
    Replace,

    /// <summary>An existing file was backed up and deleted.</summary>
    Delete
}
