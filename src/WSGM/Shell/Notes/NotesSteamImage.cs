namespace WSGM.Shell;

/// <summary>An image for Steam's page.</summary>
/// <param name="Asset">The asset id.</param>
/// <param name="DataUrl">The image as a data URL.</param>
internal sealed record NotesSteamImage(string Asset, string DataUrl);
