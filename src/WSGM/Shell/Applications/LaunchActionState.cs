using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Fresh launch configuration shared by the Overlay and Steam game page.</summary>
internal sealed record LaunchActionState(uint AppId, string Name, bool IsShortcut, string Ownership,
    bool CanRestore, bool WrapperAvailable, bool ShimManaged, bool ExternallyChanged,
    string Target, string Options, string InputRoute, string? Error = null, string? Notice = null)
{
    public IReadOnlyList<LaunchActionOption> Actions => WrapperActions;

    private static readonly IReadOnlyList<LaunchActionOption> WrapperActions =
    [
        new LaunchActionOption((int)LaunchWrapperMode.Deelevate, LaunchActionService.Label(LaunchWrapperMode.Deelevate)),
        new LaunchActionOption((int)LaunchWrapperMode.InputLease, LaunchActionService.Label(LaunchWrapperMode.InputLease)),
        new LaunchActionOption((int)LaunchWrapperMode.Both, LaunchActionService.Label(LaunchWrapperMode.Both))
    ];
}
