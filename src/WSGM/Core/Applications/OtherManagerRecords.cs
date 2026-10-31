using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A handheld manager WSGM's Full mode replaces, and how it starts itself.</summary>
/// <param name="Id">Stable id, for the log and the records.</param>
/// <param name="Label">Product name shown in setup.</param>
/// <param name="Processes">Its process names, without extension. A logon task running one of them is its autostart.</param>
/// <param name="Services">Its Windows services.</param>
/// <param name="Tasks">Its scheduled tasks, by name in the root folder.</param>
public sealed record OtherManager(
    string Id,
    string Label,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Tasks)
{
    /// <summary>Exact MSIX package families whose activation Full mode blocks.</summary>
    public IReadOnlyList<string> PackageFamilies { get; init; } = [];

    /// <summary>One dedicated keyboard hardware identity; the physical machine must also match.</summary>
    public string? DedicatedKeyboardDeviceId { get; init; }

    /// <summary>Exact LibHandheld definition required before this manager's targets are considered.</summary>
    public string? DeviceDefinitionId { get; init; }
}

/// <summary>A manager found on this machine and what of it would start.</summary>
/// <param name="Manager">Which manager.</param>
/// <param name="Services">Its services that are not disabled.</param>
/// <param name="Tasks">Its enabled tasks, by path.</param>
/// <param name="Running">Its processes running now.</param>
public sealed record DetectedManager(
    OtherManager Manager,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> Running)
{
    /// <summary>Whether anything of it would start or runs now.</summary>
    public bool Active => Services.Count > 0 || Tasks.Count > 0 || Running.Count > 0 || Targets.Count > 0;

    /// <summary>Package activation and dedicated device changes, including their previous states.</summary>
    public IReadOnlyList<OtherManagerRecord> Targets { get; init; } = [];

    /// <summary>Whether every one of its tasks has a sign-in trigger.</summary>
    public bool StartsAtSignIn { get; init; }

    /// <summary>One line for setup, such as <c>MSI Center M: 1 service, 2 scheduled tasks</c>.</summary>
    /// <returns>The product label followed by active service/task counts and running-state text.</returns>
    public string Describe()
    {
        List<string> parts = [];
        if (Services.Count > 0)
        {
            parts.Add(Services.Count == 1 ? "1 service" : $"{Services.Count} services");
        }

        if (Tasks.Count > 0)
        {
            parts.Add(Tasks.Count == 1
                ? StartsAtSignIn ? "starts at sign-in" : "1 scheduled task"
                : $"{Tasks.Count} scheduled tasks");
        }

        if (Running.Count > 0)
        {
            parts.Add("running now");
        }

        if (Targets.Count > 0)
        {
            parts.Add($"{Targets.Count} package or keyboard settings");
        }

        return parts.Count == 0 ? Manager.Label : $"{Manager.Label}: {string.Join(", ", parts)}";
    }
}

/// <summary>One manager startup, package activation or dedicated keyboard setting and how to put it back.</summary>
/// <remarks>
///     An install-lifecycle recovery snapshot like <see cref="SteamAutostartRecord" />: recorded before the
///     change, read by the uninstaller to undo exactly what WSGM changed, and removed once restored.
/// </remarks>
public sealed class OtherManagerRecord
{
    /// <summary>The manager it belongs to.</summary>
    public string ManagerId { get; set; } = "";

    /// <summary><c>service</c>, <c>task</c>, <c>package</c> or <c>device</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Service name or task path.</summary>
    public string Name { get; set; } = "";

    /// <summary>Service start type, or a package's prior disabled bit (0 enabled, 1 disabled).</summary>
    public int PreviousStart { get; set; }

    /// <summary>Whether a service started delayed before WSGM changed it.</summary>
    public bool PreviousDelayed { get; set; }
}

/// <summary>What turning the managers off achieved.</summary>
/// <param name="Disabled">Services and tasks now off.</param>
/// <param name="Failed">Services and tasks that could not be changed.</param>
/// <param name="StillRunning">Manager windows that did not close when asked; setup never ends them.</param>
public sealed record OtherManagersResult(
    IReadOnlyList<string> Disabled,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> StillRunning)
{
    /// <summary>Windows requires a restart to finish a device change or close a blocked packaged helper.</summary>
    public bool RestartRequired { get; init; }
}
