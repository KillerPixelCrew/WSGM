namespace WSGM.Core;

/// <summary>One installed package, as the enumerator reports it.</summary>
/// <param name="Aumid">Its application user model id.</param>
/// <param name="FamilyName">Its package family name.</param>
/// <param name="DisplayName">The name Windows shows for it, or empty.</param>
/// <param name="InstallPath">Where its files are, or empty when unreadable.</param>
/// <param name="ApplicationId">The application half of the AUMID.</param>
public sealed record InstalledPackage(
    string Aumid,
    string FamilyName,
    string DisplayName,
    string InstallPath,
    string ApplicationId);
