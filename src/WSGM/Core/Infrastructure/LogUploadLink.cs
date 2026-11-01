namespace WSGM.Core;

/// <summary>One uploaded paste: a whole log file, or one piece of it in order.</summary>
/// <param name="FileName">The log file the paste holds part of.</param>
/// <param name="Piece">Its 1-based piece number within that file.</param>
/// <param name="Url">The paste's address.</param>
internal sealed record LogUploadLink(string FileName, int Piece, string Url);
