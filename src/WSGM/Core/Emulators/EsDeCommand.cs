namespace WSGM.Core;

/// <summary>One launch command of an ES-DE system, which ES-DE lists as an alternative emulator by its label.</summary>
/// <param name="Label">The emulator, or the RetroArch core, as ES-DE names the choice.</param>
/// <param name="Command">The Windows command line with ES-DE's game variables.</param>
/// <param name="InstallationId">The installation the command starts.</param>
/// <param name="CoreId">The RetroArch core it loads, or empty.</param>
internal sealed record EsDeCommand(string Label, string Command, string InstallationId, string CoreId);
