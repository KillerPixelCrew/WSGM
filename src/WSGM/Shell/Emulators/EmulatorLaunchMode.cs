using System.Collections.Generic;
using System.Linq;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One launch mode an emulator's Steam shortcut can use, as both surfaces list it.</summary>
/// <param name="Value">The <see cref="LaunchWrapperMode" /> the shortcut command carries.</param>
/// <param name="Label">The launch action's name, as the game launch configuration calls it.</param>
internal sealed record EmulatorLaunchMode(int Value, string Label)
{
    /// <summary>The modes an emulator shortcut offers, in the order both surfaces list them.</summary>
    internal static IReadOnlyList<EmulatorLaunchMode> Offered { get; } =
    [
        .. new[]
        {
            LaunchWrapperMode.InputLease, LaunchWrapperMode.None, LaunchWrapperMode.Deelevate,
            LaunchWrapperMode.Both
        }.Select(mode => new EmulatorLaunchMode((int)mode, LaunchActionService.Label(mode)))
    ];
}
