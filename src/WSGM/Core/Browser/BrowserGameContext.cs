namespace WSGM.Core;

/// <summary>Locally resolved context offered only when the user chooses game-aware search.</summary>
/// <param name="AppId">The game's stable Steam identifier.</param>
/// <param name="Title">Display title; never transmitted automatically.</param>
internal sealed record BrowserGameContext(string AppId, string Title);
