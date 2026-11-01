namespace WSGM.Core;

/// <summary>One deduplicated requester within a lock kind.</summary>
/// <param name="Label">Short name, e.g. <c>steam.exe</c>.</param>
/// <param name="Detail">Caller kind, pid and full path, for the row's description.</param>
/// <param name="Reason">The requester's reason string, when it supplied one.</param>
/// <param name="Count">How many identical requests collapsed into this row.</param>
public sealed record WakeLockHolder(string Label, string Detail, string? Reason, int Count);
