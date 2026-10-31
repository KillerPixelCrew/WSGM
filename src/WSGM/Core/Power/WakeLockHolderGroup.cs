using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>The holders of one kind of lock, in the order they should be listed.</summary>
/// <param name="Title">User-facing name of the lock kind.</param>
/// <param name="Holders">Deduplicated requesters, most numerous first.</param>
public sealed record WakeLockHolderGroup(string Title, IReadOnlyList<WakeLockHolder> Holders);
