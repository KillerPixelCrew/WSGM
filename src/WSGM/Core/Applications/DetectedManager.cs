using System.Collections.Generic;

namespace WSGM.Core;

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
