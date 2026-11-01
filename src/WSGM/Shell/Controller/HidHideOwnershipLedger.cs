using System.Collections.Generic;

namespace WSGM.Shell;

/// <summary>The entries WSGM added, persisted so a crash still leaves them for the next exit to remove.</summary>
internal sealed class HidHideOwnershipLedger
{
    public List<HidHideOwnedDelta> Deltas { get; init; } = [];
}
