namespace WSGM.Core;

/// <summary>The log file an upload is sending now.</summary>
/// <param name="FileName">Its name, such as <c>wsgm.log</c>.</param>
internal sealed record LogUploadProgress(string FileName);
