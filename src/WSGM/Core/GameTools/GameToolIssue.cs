namespace WSGM.Core;

/// <summary>A file that blocks installation or needs review.</summary>
/// <param name="RelativePath">The file relative to the target directory.</param>
/// <param name="Kind"><c>unmanagedTool</c> or <c>needsReview</c>.</param>
/// <param name="Message">The user-facing explanation.</param>
internal sealed record GameToolIssue(string RelativePath, string Kind, string Message);
