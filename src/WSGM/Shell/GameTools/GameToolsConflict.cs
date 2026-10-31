namespace WSGM.Shell;

/// <summary>A file that blocks installation or needs review.</summary>
/// <param name="Path">The file relative to the target folder.</param>
/// <param name="Kind"><c>unmanagedTool</c> or <c>needsReview</c>.</param>
/// <param name="KindLabel">The kind shown to the user.</param>
/// <param name="Message">The explanation.</param>
internal sealed record GameToolsConflict(string Path, string Kind, string KindLabel, string Message);
