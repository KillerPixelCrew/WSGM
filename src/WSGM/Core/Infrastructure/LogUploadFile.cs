namespace WSGM.Core;

/// <summary>One log file an upload sends.</summary>
/// <param name="Name">The file name, such as <c>wsgm.log</c>.</param>
/// <param name="Path">The full path.</param>
/// <param name="Bytes">Its size when it was listed.</param>
/// <param name="Previous">Whether it is the rotated archive, which holds the previous session.</param>
internal sealed record LogUploadFile(string Name, string Path, long Bytes, bool Previous);
