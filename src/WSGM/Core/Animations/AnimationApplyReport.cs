namespace WSGM.Core;

/// <summary>What applying a choice did.</summary>
/// <param name="Changed">Whether the override was written, removed or given back.</param>
/// <param name="Error">Why it could not be brought in step, or null.</param>
/// <param name="Note">Something about the written movie worth saying, such as a volume that could not be set.</param>
public sealed record AnimationApplyReport(bool Changed, string? Error, string? Note = null);
