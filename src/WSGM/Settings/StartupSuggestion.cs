namespace WSGM.Settings;

/// <summary>A companion app found on this PC, offered when adding a startup app.</summary>
/// <param name="Label">What the app is called.</param>
/// <param name="Path">Its program.</param>
/// <param name="Elevated">Whether it needs to run as administrator.</param>
public sealed record StartupSuggestion(string Label, string Path, bool Elevated);
