namespace WSGM.Core;

/// <summary>The title fields a game's PARAM.SFO declares.</summary>
/// <param name="Title">The TITLE entry.</param>
/// <param name="TitleId">The TITLE_ID entry.</param>
/// <param name="Category">The CATEGORY entry.</param>
internal sealed record SfoMetadata(string Title, string TitleId, string Category);
