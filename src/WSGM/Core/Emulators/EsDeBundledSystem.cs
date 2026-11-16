namespace WSGM.Core;

/// <summary>A system as the installed ES-DE's own Windows configuration names it.</summary>
/// <param name="Name">The short name, such as <c>psx</c>.</param>
/// <param name="FullName">The name ES-DE shows, such as <c>Sony PlayStation</c>.</param>
/// <param name="Platform">The scraper platform.</param>
/// <param name="Theme">The theme name.</param>
internal sealed record EsDeBundledSystem(string Name, string FullName, string Platform, string Theme);
