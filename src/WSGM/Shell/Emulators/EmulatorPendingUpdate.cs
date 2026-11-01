namespace WSGM.Shell;

/// <summary>An installed emulator whose channel offers a newer release that was not skipped.</summary>
/// <param name="InstallationId">The installation the update command takes.</param>
/// <param name="Name">The emulator's name.</param>
/// <param name="Version">The offered version.</param>
internal sealed record EmulatorPendingUpdate(string InstallationId, string Name, string Version);
