using System;

namespace WSGM.Core;

internal enum ExplorerExitAction
{
    Wait,
    ReleaseOriginal,
    Complete
}

/// <summary>Only a retired shell can be released. A live desktop is never force-closed by entry.</summary>
internal static class ExplorerExitPolicy
{
    internal static ExplorerExitAction Decide(bool shellSurfacePresent, bool originalExited, TimeSpan absentFor)
    {
        if (shellSurfacePresent)
        {
            return ExplorerExitAction.Wait;
        }

        if (originalExited && absentFor >= TimeSpan.FromMilliseconds(500))
        {
            return ExplorerExitAction.Complete;
        }

        return !originalExited && absentFor >= TimeSpan.FromSeconds(2)
            ? ExplorerExitAction.ReleaseOriginal
            : ExplorerExitAction.Wait;
    }
}
