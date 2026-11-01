namespace WSGM.Core;

/// <summary>One explicitly selected artwork revision handled by an account's shortcut.</summary>
/// <param name="Url">The selected image URL, or empty for a clear.</param>
/// <param name="Revision">The explicit selection identity, retained across scans.</param>
public sealed record AppliedArtworkChoice(string Url, string Revision);
