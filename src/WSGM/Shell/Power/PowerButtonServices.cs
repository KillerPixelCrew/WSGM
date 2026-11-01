using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What the session lets a power-button press do; supplied once the shell surfaces exist.</summary>
internal sealed record PowerButtonServices
{
    /// <summary>Whether the session is in Game Mode now.</summary>
    internal required Func<bool> InGameMode { get; init; }

    /// <summary>Opens Steam's Big Picture power menu; false when Steam could not be asked.</summary>
    internal required Func<CancellationToken, Task<bool>> OpenSteamPowerMenuAsync { get; init; }

    /// <summary>Opens the overlay's power menu, or closes it when it is already open.</summary>
    internal required Func<CancellationToken, Task<bool>> ToggleDesktopPowerMenuAsync { get; init; }

    /// <summary>Closes the overlay's power menu when it is open; true means a short press only closed it.</summary>
    internal required Func<CancellationToken, Task<bool>> CloseDesktopPowerMenuAsync { get; init; }

    /// <summary>Runs Steam's own suspend screen without its PC suspend.</summary>
    internal required Func<CancellationToken, Task<SteamSuspendFlowResult>> RunSteamSuspendFlowAsync { get; init; }

    /// <summary>Tells Steam the PC resumed, once its UI can be reached again; false when cancelled first.</summary>
    internal required Func<CancellationToken, Task<bool>> NotifySteamResumedAsync { get; init; }

    /// <summary>Enters Modern Standby the way the display timeout does.</summary>
    internal required Func<bool> EnterStandby { get; init; }

    /// <summary>Requests hibernate, restart or shut down through the session's power actions.</summary>
    internal required Action<SessionPowerAction> RequestPowerAction { get; init; }
}
