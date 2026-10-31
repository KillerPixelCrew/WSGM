namespace WSGM.Core;

/// <summary>What a scan knows about the source a record came from.</summary>
public enum ImportSourceState
{
    /// <summary>Read in full in this scan: a record whose title it did not report is gone.</summary>
    Read,

    /// <summary>
    ///     Not read, or not trusted to be complete: unticked, failed, or reporting nothing although
    ///     titles were imported from it. Its records are left out, because turning a launcher off or
    ///     a transient failure is not a request to delete its games.
    /// </summary>
    Unread,

    /// <summary>No longer on this machine: an uninstalled launcher or a removed folder.</summary>
    Gone
}
