namespace WSGM.Core;

/// <summary>One game Ubisoft Connect lists as installed.</summary>
/// <param name="Id">The registry key's name, the game's Ubisoft id.</param>
/// <param name="InstallDir">Where it is installed, as the launcher wrote it.</param>
public sealed record UbisoftInstall(string Id, string InstallDir);
