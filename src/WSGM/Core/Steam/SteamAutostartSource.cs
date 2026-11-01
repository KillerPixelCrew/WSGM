namespace WSGM.Core;

/// <summary>One place Windows starts Steam from.</summary>
/// <param name="Kind">What kind of source this is.</param>
/// <param name="Scope">Whether changing it needs elevation.</param>
/// <param name="Location">Registry key, folder or task path, for the log and the record.</param>
/// <param name="Name">The value name, file name or task name inside that location.</param>
/// <param name="Enabled">Whether Windows would act on it as it stands.</param>
/// <param name="Wow64">
///     Whether a run value lives in the 32-bit registry view, which Windows
///     approves through its own <c>Run32</c> list.
/// </param>
public sealed record SteamAutostartSource(
    SteamAutostartKind Kind,
    SteamAutostartScope Scope,
    string Location,
    string Name,
    bool Enabled,
    bool Wow64 = false)
{
    /// <summary>Whether disabling this source needs an elevated process.</summary>
    public bool NeedsElevation => Scope is SteamAutostartScope.Machine;

    /// <summary>A short description for setup's takeover list and the log.</summary>
    /// <returns>A compact source-kind and scope description suitable for takeover diagnostics.</returns>
    public string Describe()
    {
        return Kind switch
        {
            SteamAutostartKind.RunValue => $"Startup entry \"{Name}\" ({Location})",
            SteamAutostartKind.StartupShortcut => $"Startup shortcut \"{Name}\"",
            _ => $"Scheduled task \"{Location}\""
        };
    }
}
