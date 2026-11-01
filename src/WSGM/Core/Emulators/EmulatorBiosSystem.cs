namespace WSGM.Core;

internal sealed record EmulatorBiosSystem(
    string Id,
    string Name,
    string Status,
    string[] Emulators,
    EmulatorBiosFile[] Files,
    string[] Links)
{
    /// <summary>Whether any one of the files satisfies the system, as regional alternatives do.</summary>
    public bool Any { get; init; }
}
