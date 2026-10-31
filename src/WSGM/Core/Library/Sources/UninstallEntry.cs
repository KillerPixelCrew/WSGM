namespace WSGM.Core;

/// <summary>One program in Windows' installed-programs list.</summary>
/// <param name="KeyName">The registry key's own name, which some launchers use as an id.</param>
/// <param name="DisplayName">What Windows calls it.</param>
/// <param name="InstallLocation">Where it says it is installed, or empty.</param>
/// <param name="Publisher">Who published it, or empty.</param>
/// <param name="UninstallString">The command that removes it, or empty.</param>
/// <param name="DisplayIcon">Its icon, often the main executable, or empty.</param>
public sealed record UninstallEntry(
    string KeyName,
    string DisplayName,
    string InstallLocation,
    string Publisher,
    string UninstallString,
    string DisplayIcon);
