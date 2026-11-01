namespace WSGM.Core;

/// <summary>The paste an upload is sending now.</summary>
/// <param name="FileName">The log file it holds part of.</param>
/// <param name="Piece">Its 1-based piece number within that file.</param>
internal sealed record LogUploadProgress(string FileName, int Piece);
