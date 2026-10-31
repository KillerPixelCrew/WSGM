using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Fresh launch configuration shared by the Overlay and Steam game page.</summary>
internal sealed record LaunchActionState(
    uint AppId,
    string Name,
    bool IsShortcut,
    string Ownership,
    bool CanRestore,
    bool WrapperAvailable,
    bool ShimManaged,
    bool ExternallyChanged,
    string Target,
    string Options,
    string InputRoute,
    string? Error = null,
    string? Notice = null)
{
    private static readonly IReadOnlyList<LaunchActionOption> WrapperActions =
    [
        new((int)LaunchWrapperMode.Deelevate, LaunchActionService.Label(LaunchWrapperMode.Deelevate)),
        new((int)LaunchWrapperMode.InputLease, LaunchActionService.Label(LaunchWrapperMode.InputLease)),
        new((int)LaunchWrapperMode.Both, LaunchActionService.Label(LaunchWrapperMode.Both))
    ];

    public IReadOnlyList<LaunchActionOption> Actions => WrapperActions;
}
