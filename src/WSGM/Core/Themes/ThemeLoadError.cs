namespace WSGM.Core;

/// <summary>A folder the loader refused, and why.</summary>
/// <param name="Folder">The folder's name.</param>
/// <param name="Error">The reason.</param>
public sealed record ThemeLoadError(string Folder, string Error);
