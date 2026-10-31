using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Projects WSGM's own controller management into Steam's native quick-access menu.
/// </summary>
/// <remarks>
///     The controller target is WSGM's setting, not a plugin capability, so this reads
///     <see cref="ControllerManager" /> through the coordinator instead of looking for a capability
///     descriptor. That keeps one owner: the QAM control and the overlay's controller page move the same
///     stored default through the same method, and neither holds a copy of the target. A null
///     coordinator projects the constant unavailable state and refuses every write.
/// </remarks>
internal sealed class DeviceCoordinatorNativeQamControllerTargetService :
    ISteamControllerTargetBackend,
    IDisposable
{
    /// <summary>
    ///     Why the row is inert, stated as the one cause that can still produce it: the
    ///     session is running without device integration. Every build ships the component.
    /// </summary>
    internal const string UnavailableDetail =
        "Controller management is unavailable: this session is running without device integration.";

    /// <summary>What the row adds to its status while a running game holds the target it launched with.</summary>
    internal const string RestartToRebind = "Restart the application to rebind.";

    private static readonly SteamControllerTargetState UnavailableState = new(
        false,
        [],
        string.Empty,
        string.Empty,
        string.Empty,
        UnavailableDetail);

    private readonly DeviceCoordinator? _coordinator;
    private bool _disposed;

    /// <summary>Creates the projection over a running coordinator, or the unavailable one.</summary>
    /// <param name="coordinator">The coordinator owning controller management, or null.</param>
    internal DeviceCoordinatorNativeQamControllerTargetService(DeviceCoordinator? coordinator)
    {
        _coordinator = coordinator;
        if (_coordinator is not null)
        {
            _coordinator.Controllers.StatusChanged += OnControllerStatusChanged;
        }
    }

    /// <summary>The state Steam should currently be rendering.</summary>
    public SteamControllerTargetState Current => _coordinator is null
        ? UnavailableState
        : Project(
            _coordinator.ControllerManagementEnabled,
            _coordinator.Controllers.Snapshot(),
            _coordinator.HasDevice,
            _coordinator.Controllers.SupportedTargets,
            _coordinator.ChosenControllerTarget());

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.Controllers.StatusChanged -= OnControllerStatusChanged;
        }
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetControllerTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        return SetTargetAsync(target, cancellationToken);
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Stores and applies the chosen managed-controller target.</summary>
    private async Task<SteamUiCommandResult> SetTargetAsync(
        string target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = Current;
        if (_coordinator is null || !state.Available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SteamUiCommandResult(false, state.StatusText);
        }

        if (!TryParseTarget(target, out var parsed))
        {
            return new SteamUiCommandResult(false, $"'{target}' is not a controller target.");
        }

        var status = await _coordinator
            .SetControllerTargetAsync(parsed, cancellationToken)
            .ConfigureAwait(false);

        // Truthful rather than optimistic: the setting is stored either way, but a manager that
        // could not bring the new target up is not a success the menu should show as one.
        var succeeded = status.State is not
            (ControllerManagementState.Faulted or ControllerManagementState.Unavailable);
        return new SteamUiCommandResult(succeeded, succeeded ? null : status.Detail);
    }

    /// <summary>Projects controller state into the menu's closed vocabulary.</summary>
    /// <param name="enabled">Whether controller management may run at all.</param>
    /// <param name="status">The manager's current truthful state.</param>
    /// <param name="packageInstalled">Whether a native handheld definition is available.</param>
    /// <param name="supportedTargets">Targets the backend on this machine can create.</param>
    /// <param name="chosen">The stored choice, shown as selected while no target is live.</param>
    /// <returns>The state the menu renders.</returns>
    internal static SteamControllerTargetState Project(
        bool enabled,
        ControllerManagerStatus status,
        bool packageInstalled,
        IReadOnlyList<ManagedControllerTarget> supportedTargets,
        ManagedControllerTarget? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(supportedTargets);
        if (!enabled)
        {
            return new SteamControllerTargetState(
                false,
                [],
                string.Empty,
                string.Empty,
                string.Empty,
                NativeQamUi.Text(status.Detail));
        }

        // Only what the backend can actually build. These are WSGM's own virtual devices rather
        // than hardware, but a target still needs an encoder behind it: offering one that has none
        // meant the selection persisted, target creation was refused, and controller management
        // reported itself unavailable until the user found their way back to the setting.
        SteamControllerTargetOption[] targets =
        [
            .. new[]
                {
                    (Target: ManagedControllerTarget.SteamDeckComposite, Label: "Steam Deck"),
                    (Target: ManagedControllerTarget.Xbox360, Label: "Xbox 360"),
                    (Target: ManagedControllerTarget.DualShock4, Label: "DualShock 4")
                }
                .Where(option => supportedTargets.Contains(option.Target))
                .Select(option => new SteamControllerTargetOption(
                    option.Target.ToString(),
                    option.Label,
                    true))
        ];

        var available = status.State is
            ControllerManagementState.Idle or ControllerManagementState.Active or ControllerManagementState.Native;
        var selected = (status.Target ?? chosen) is { } target ? target.ToString() : string.Empty;

        // Observed is what a target actually exists for right now: the virtual one while Active, the
        // physical pad itself while Native. Reporting the selection back as if it were observed would
        // hide a target that was chosen but never came up.
        var observed = status.State is ControllerManagementState.Active or ControllerManagementState.Native
            ? selected
            : string.Empty;
        var detail = status.Detail;
        if (available && string.IsNullOrWhiteSpace(detail) && !packageInstalled)
        {
            detail =
                "This machine has no supported handheld implementation, so no physical controller is being captured.";
        }

        // A running game holds the target it was launched with, so a change reaches it only on the
        // next launch. Saying so is the difference between a control that looks broken and one the
        // user understands.
        var statusText = NativeQamUi.Text(detail);
        if (status.ApplicationId is not null)
        {
            statusText = statusText.Length > 0 ? $"{statusText} {RestartToRebind}" : RestartToRebind;
        }

        return new SteamControllerTargetState(
            available,
            targets,
            selected,
            observed,
            ProgressFor(status.State),
            statusText,
            status.TargetSource is ProfileSource.Game);
    }

    /// <summary>Maps a stored target name back onto the enumeration.</summary>
    /// <param name="target">The name the menu sent.</param>
    /// <param name="parsed">Receives the parsed target.</param>
    /// <returns>Whether the name named a target.</returns>
    /// <remarks>
    ///     Ordinal and case-sensitive on purpose: the menu is sent these names from
    ///     <see cref="Project" />, so anything else is a caller defect rather than user input to be
    ///     forgiving about.
    /// </remarks>
    internal static bool TryParseTarget(string target, out ManagedControllerTarget parsed)
    {
        return Enum.TryParse(target, false, out parsed)
               && Enum.IsDefined(parsed);
    }

    private static string ProgressFor(ControllerManagementState state)
    {
        return state switch
        {
            ControllerManagementState.Active or ControllerManagementState.Native => "completed",
            ControllerManagementState.Idle => string.Empty,
            ControllerManagementState.Faulted => "failed",
            _ => string.Empty
        };
    }

    private void OnControllerStatusChanged(ControllerManagerStatus status)
    {
        StateChanged?.Invoke();
    }
}
