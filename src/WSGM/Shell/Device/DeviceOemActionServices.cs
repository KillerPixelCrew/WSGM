using System;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Shell;

/// <summary>WSGM-owned typed action surface available to the OEM router.</summary>
internal sealed record DeviceOemActionServices
{
    internal Func<bool> WrapperInputLeaseActive { get; init; } = static () => false;

    internal required Func<bool, CancellationToken, Task<bool>> SendSteamShortcutAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleOverlayAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleSteamQuickAccessAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleSteamOverlayAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleDevicePageAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleOpenAppsAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleDesktopGameModeAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> ToggleOnScreenKeyboardAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> CyclePerformanceProfileAsync { get; init; }

    internal required Func<CancellationToken, Task<bool>> CyclePerformanceOverlayLevelAsync { get; init; }

    internal required Func<int, CancellationToken, Task<bool>> SetRearButtonAsync { get; init; }
}
