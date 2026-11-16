namespace WSGM.Shell;

/// <summary>One ROM source as the ES-DE page lists it, with whether Library Importer also offers it to Steam.</summary>
/// <param name="SourceId">The ROM source, the identity Library Importer's source switch takes.</param>
/// <param name="Name">The source's name.</param>
/// <param name="Location">The configured folder.</param>
/// <param name="Systems">The systems ES-DE shows from it, joined, or what is wrong with it.</param>
/// <param name="Connected">Whether its storage is connected now.</param>
/// <param name="InSteam">Whether Library Importer offers its games as Steam shortcuts.</param>
internal sealed record EsDeFolderRow(
    string SourceId,
    string Name,
    string Location,
    string Systems,
    bool Connected,
    bool InSteam);
