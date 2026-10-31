namespace WSGM.Shell;

/// <summary>One compatible core already labelled by the host.</summary>
/// <param name="Id">The installed core identity.</param>
/// <param name="Label">The name and any missing-metadata explanation.</param>
public sealed record RomEmulatorCoreChoice(string Id, string Label);
