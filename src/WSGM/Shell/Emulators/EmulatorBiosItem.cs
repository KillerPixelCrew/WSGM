namespace WSGM.Shell;

/// <summary>
///     One line of a system's BIOS checklist: a file, or the set of regional alternatives none of which is present.
/// </summary>
/// <param name="Label">The file's path in the BIOS folder, or "Any … BIOS (for example …)".</param>
/// <param name="State"><c>verified</c>, <c>present</c>, <c>missing</c> or <c>wrongFile</c>.</param>
/// <param name="StateLabel">The state as both surfaces word it.</param>
/// <param name="Optional">Whether the system starts without it.</param>
internal sealed record EmulatorBiosItem(string Label, string State, string StateLabel, bool Optional);
