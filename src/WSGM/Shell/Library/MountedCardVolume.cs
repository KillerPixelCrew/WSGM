namespace WSGM.Shell;

/// <summary>The volume a tracked library currently lives on.</summary>
/// <param name="Root">
///     The volume GUID path with its trailing separator, which is
///     what every subsequent write addresses.
/// </param>
/// <param name="Letter">
///     The drive letter it was found through, for log lines
///     only — it is not a durable name for the volume.
/// </param>
internal readonly record struct MountedCardVolume(string Root, char Letter);
