namespace WSGM.Shell;

/// <summary>One status an installed emulator's card shows, derived once for both surfaces.</summary>
/// <param name="Kind">
///     <c>update</c>, <c>upToDate</c>, <c>setup</c>, <c>external</c>, <c>notChecked</c>, <c>checkFailed</c> or
///     <c>skipped</c>.
/// </param>
/// <param name="Label">The chip's text, such as "Update to 2.7" or "BIOS missing".</param>
/// <param name="Tone">The shared badge tone: <c>action</c>, <c>good</c>, <c>warn</c>, <c>bad</c> or empty for neutral.</param>
internal sealed record EmulatorStatusChip(string Kind, string Label, string Tone);
