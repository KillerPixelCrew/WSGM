namespace WSGM.Core;

/// <summary>A directory link in WSGM's ES-DE ROM folder that shows one ROM folder to ES-DE.</summary>
/// <param name="Path">The link, relative to the ES-DE ROM folder: the system's name, or the system and the source.</param>
/// <param name="Target">
///     The ROM folder on its current mount, or empty while its storage is not connected: an existing link is then
///     kept as it is, so a card that comes back under the same letter shows at once.
/// </param>
internal sealed record EsDeLink(string Path, string Target);
