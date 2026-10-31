using System.Collections.Generic;

namespace WSGM.Core;

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
