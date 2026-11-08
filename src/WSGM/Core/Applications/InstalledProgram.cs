namespace WSGM.Core;

/// <summary>One application the Add program list offers.</summary>
/// <param name="Key">The application's stable identity in the list, which also names its icon.</param>
/// <param name="Name">The name Windows shows.</param>
/// <param name="AppUserModelId">The packaged application's user model id, or null for a desktop application.</param>
/// <param name="Executable">The desktop application's executable, or null for a packaged one.</param>
/// <param name="Arguments">The arguments of a desktop application's shortcut, or an empty string.</param>
public sealed record InstalledProgram(
    string Key,
    string Name,
    string? AppUserModelId,
    string? Executable,
    string Arguments);
