using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Interop;
using HapticCapabilities = LibHandheld.Contracts.HapticCapabilities;
using OemControlDescriptor = LibHandheld.Contracts.OemControlDescriptor;
using OemControlEdge = LibHandheld.Contracts.OemControlEdge;
using OemControlEvent = LibHandheld.Contracts.OemControlEvent;
using OemControlPlacement = LibHandheld.Contracts.OemControlPlacement;
using OemDefaultActionHint = LibHandheld.Contracts.OemControlDefaultActionHint;
using PhysicalDeviceIdentity = LibHandheld.Contracts.PhysicalDeviceIdentity;

namespace WSGM.Shell;

/// <summary>WSGM-owned typed action surface available to the OEM router.</summary>
internal sealed record DeviceOemActionServices
{
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

/// <summary>WSGM-owned assignment and runtime-availability policy for physical OEM controls.</summary>
internal static class OemActionRules
{
    internal static bool IsAssignable(OemAction action, OemControlPlacement placement)
    {
        return !IsVirtualTargetButton(action) || placement is OemControlPlacement.Rear;
    }

    internal static bool IsVirtualTargetButton(OemAction action)
    {
        return action
            is OemAction.VirtualTargetRearButton1
            or OemAction.VirtualTargetRearButton2;
    }

    internal static bool IsAvailable(OemAction action, bool targetHasRearButtons)
    {
        return !IsVirtualTargetButton(action) || targetHasRearButtons;
    }
}

/// <summary>Maps canonical OEM events to the closed WSGM-owned action vocabulary.</summary>
internal sealed class DeviceOemActionRouter : IDisposable
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, OemControlDescriptor> _controls = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _dispatches = [];
    private readonly Lock _gate = new();

    private readonly Dictionary<string, (string PressId, DateTimeOffset Timestamp)> _heldMouseControls =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, DateTimeOffset> _recentEvents = new(StringComparer.Ordinal);
    private readonly Func<bool, bool> _setSecondaryButton;
    private long _actionGeneration;
    private DeviceOemActionServices? _actions;
    private IReadOnlyList<DeviceOemAssignment> _assignments = [];
    private HandheldDeviceRuntime? _client;
    private bool _controllerManagementEnabled;
    private bool _disposed;
    private Action<HandheldRuntimeState>? _lifecycleHandler;
    private bool _mouseAdmission = true;
    private bool _secondaryButtonDown;
    private bool _targetHasRearButtons;

    internal DeviceOemActionRouter(Func<bool, bool>? setSecondaryButton = null)
    {
        _setSecondaryButton = setSecondaryButton ?? MouseInput.SetSecondaryButton;
    }

    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return Task.WhenAll(_dispatches);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DetachUnderGate();
            ResetUnderGate();
        }

        Log.Observe(_lifetime.CancelAsync(), "OEM action cancellation");
    }

    internal void ConfigureActions(DeviceOemActionServices actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        lock (_gate)
        {
            _actions = actions;
        }
    }

    internal void Attach(HandheldDeviceRuntime client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            _mouseAdmission = false;
            ResetUnderGate();
            client.OemControlsReceived += OnControls;
            client.OemEventReceived += OnEvent;
            client.PhysicalIdentitiesReceived += OnPhysicalDevices;
            _lifecycleHandler = state => OnLifecycle(client, state);
            client.LifecycleStateReceived += _lifecycleHandler;
        }
    }

    internal void UpdateConfiguration(
        IReadOnlyList<DeviceOemAssignment> assignments,
        bool controllerManagementEnabled,
        ManagedControllerTarget target)
    {
        lock (_gate)
        {
            _assignments = assignments;
            _controllerManagementEnabled = controllerManagementEnabled;
            _targetHasRearButtons = target is ManagedControllerTarget.SteamDeckComposite;
            _actionGeneration++;
            ResetUnderGate();
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            ResetUnderGate();
        }
    }

    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
            ResetUnderGate();
        }
    }

    internal void OnControls(IReadOnlyList<OemControlDescriptor> controls)
    {
        lock (_gate)
        {
            if (controls.Any(control => !ValidControl(control))
                || controls.Select(control => control.ControlId)
                    .Distinct(StringComparer.Ordinal).Count() != controls.Count)
            {
                Log.Warn("Device OEM control set rejected as malformed or duplicated.");
                return;
            }

            _controls.Clear();
            foreach (var control in controls)
            {
                _controls.Add(control.ControlId, control);
            }

            _actionGeneration++;
            ResetUnderGate();
        }
    }

    internal void OnEvent(OemControlEvent input)
    {
        OemAction action;
        DeviceOemActionServices? actions;
        CancellationToken cancellationToken;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            cancellationToken = _lifetime.Token;
            // A release belongs to the press admitted earlier, even if configuration or availability
            // has changed. It executes in this owner before any later press can send another edge.
            if (input.Edge is OemControlEdge.Released
                && _heldMouseControls.TryGetValue(input.ControlId, out var heldPress)
                && string.Equals(heldPress.PressId, input.DeduplicationId, StringComparison.Ordinal))
            {
                _heldMouseControls.Remove(input.ControlId);
                if (_heldMouseControls.Count == 0)
                {
                    ReleaseSecondaryButtonUnderGate();
                }

                return;
            }

            if (!_controls.TryGetValue(input.ControlId, out var control)
                || string.IsNullOrWhiteSpace(input.DeduplicationId)
                || input.Timestamp > DateTimeOffset.UtcNow.AddSeconds(5)
                || DateTimeOffset.UtcNow - input.Timestamp > DeduplicationWindow)
            {
                Log.Warn($"Device OEM event rejected: control={input.ControlId}.");
                return;
            }

            if (input.Edge is OemControlEdge.Released)
            {
                return;
            }

            var deduplicationKey =
                $"{_actionGeneration}:{input.ControlId}:{input.Press}:{input.Edge}:{input.DeduplicationId}";
            ExpireDeduplicationUnderGate(DateTimeOffset.UtcNow);
            if (!_recentEvents.TryAdd(deduplicationKey, input.Timestamp))
            {
                Log.Info($"Device OEM duplicate suppressed: control={input.ControlId}.");
                return;
            }

            action = ResolveActionUnderGate(control);
            if (!OemActionRules.IsAssignable(action, control.Placement)
                || !OemActionRules.IsAvailable(action, _targetHasRearButtons)
                || (control.RequiresControllerAcquisition && !_controllerManagementEnabled))
            {
                Log.Warn($"Device OEM action unavailable: control={control.ControlId}, action={action}.");
                return;
            }

            if (action is OemAction.MouseSecondaryButton)
            {
                if (!_mouseAdmission)
                {
                    return;
                }

                if (!_heldMouseControls.TryGetValue(input.ControlId, out var previousPress))
                {
                    if (_heldMouseControls.Count == 0 && !PressSecondaryButtonUnderGate())
                    {
                        if (_secondaryButtonDown)
                        {
                            _heldMouseControls.Add(input.ControlId, (input.DeduplicationId, input.Timestamp));
                        }

                        return;
                    }

                    _heldMouseControls.Add(input.ControlId, (input.DeduplicationId, input.Timestamp));
                }
                else if (input.Timestamp >= previousPress.Timestamp)
                {
                    _heldMouseControls[input.ControlId] = (input.DeduplicationId, input.Timestamp);
                }

                return;
            }

            actions = _actions;
        }

        if (action is OemAction.Disabled)
        {
            return;
        }

        if (actions is null)
        {
            Log.Warn($"Device OEM action unavailable before UI services attach: action={action}.");
            return;
        }

        Task dispatch;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            dispatch = Task.Run(() => DispatchAsync(actions, action, input, cancellationToken));
            _dispatches.Add(dispatch);
        }

        _ = FinishDispatchAsync(dispatch);
    }

    private async Task FinishDispatchAsync(Task dispatch)
    {
        try
        {
            await dispatch.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("OEM action dispatch failed", ex);
        }
        finally
        {
            lock (_gate)
            {
                _dispatches.Remove(dispatch);
            }
        }
    }

    private static async Task DispatchAsync(
        DeviceOemActionServices actions,
        OemAction action,
        OemControlEvent input,
        CancellationToken cancellationToken)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(3));
            var completed = action switch
            {
                OemAction.ToggleWsgmOverlay => await actions.ToggleOverlayAsync(bounded.Token)
                    .ConfigureAwait(false),
                OemAction.ToggleSteamQuickAccess =>
                    await actions.ToggleSteamQuickAccessAsync(bounded.Token).ConfigureAwait(false),
                OemAction.ToggleSteamOverlay =>
                    await actions.ToggleSteamOverlayAsync(bounded.Token).ConfigureAwait(false),
                OemAction.ShowWsgmDevicePage => await actions.ToggleDevicePageAsync(bounded.Token)
                    .ConfigureAwait(false),
                OemAction.ToggleWsgmTaskbar => await actions.ToggleOpenAppsAsync(bounded.Token)
                    .ConfigureAwait(false),
                OemAction.ToggleDesktopGameMode =>
                    await actions.ToggleDesktopGameModeAsync(bounded.Token).ConfigureAwait(false),
                OemAction.ToggleOnScreenKeyboard =>
                    await actions.ToggleOnScreenKeyboardAsync(bounded.Token).ConfigureAwait(false),
                OemAction.CyclePerformanceProfile =>
                    await actions.CyclePerformanceProfileAsync(bounded.Token).ConfigureAwait(false),
                OemAction.CyclePerformanceOverlayLevel =>
                    await actions.CyclePerformanceOverlayLevelAsync(bounded.Token).ConfigureAwait(false),
                OemAction.VirtualTargetRearButton1 =>
                    await actions.SetRearButtonAsync(1, bounded.Token).ConfigureAwait(false),
                OemAction.VirtualTargetRearButton2 =>
                    await actions.SetRearButtonAsync(2, bounded.Token).ConfigureAwait(false),
                _ => true
            };
            Log.Info($"Device OEM action: control={input.ControlId}, action={action}, "
                     + $"completed={completed}.");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Device OEM action timed out: control={input.ControlId}, action={action}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Device OEM action failed: control={input.ControlId}, action={action}", ex);
        }
    }

    private OemAction ResolveActionUnderGate(OemControlDescriptor control)
    {
        var assignment = _assignments.FirstOrDefault(item =>
            string.Equals(item.ControlId, control.ControlId, StringComparison.Ordinal));

        // The handheld's Guide and Quick Access buttons reach Steam as the virtual target's own
        // buttons: the plugin puts them in the controller sample and Steam responds natively, so WSGM
        // neither intercepts them nor synthesizes anything on their behalf. The one default WSGM
        // claims is the manufacturer's companion-application button (Armoury Crate on the Xbox Ally),
        // a button the Steam Deck layout has no place for: it opens WSGM, the companion application
        // here, as Handheld Companion opens its own window from it. An explicit assignment, Disabled
        // included, always wins.
        return assignment?.Action ?? DefaultAction(control);
    }

    /// <summary>What an unassigned control does.</summary>
    /// <param name="control">Published OEM control declaration, including placement and companion-application role.</param>
    /// <returns>Right mouse button for the tablet touchpad gesture, WSGM for a companion button, otherwise disabled.</returns>
    internal static OemAction DefaultAction(OemControlDescriptor control)
    {
        if (control.DefaultActionHint == OemDefaultActionHint.MouseSecondaryButton &&
            control.Placement is OemControlPlacement.Front)
        {
            return OemAction.MouseSecondaryButton;
        }

        return control is
            { DefaultActionHint: OemDefaultActionHint.CompanionApplication, Placement: OemControlPlacement.Front }
            ? OemAction.ToggleWsgmOverlay
            : OemAction.Disabled;
    }

    private void ExpireDeduplicationUnderGate(DateTimeOffset now)
    {
        foreach (var key in _recentEvents
                     .Where(item => now - item.Value > DeduplicationWindow)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _recentEvents.Remove(key);
        }
    }

    private void ResetUnderGate()
    {
        _heldMouseControls.Clear();
        ReleaseSecondaryButtonUnderGate();
        _recentEvents.Clear();
    }

    private bool PressSecondaryButtonUnderGate()
    {
        if (_secondaryButtonDown)
        {
            ReleaseSecondaryButtonUnderGate();
            if (_secondaryButtonDown)
            {
                return false;
            }
        }

        _secondaryButtonDown = true;
        try
        {
            if (_setSecondaryButton(true))
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Error("OEM right mouse button press failed", exception);
        }

        ReleaseSecondaryButtonUnderGate();
        return false;
    }

    private void ReleaseSecondaryButtonUnderGate()
    {
        if (!_secondaryButtonDown)
        {
            return;
        }

        try
        {
            if (_setSecondaryButton(false))
            {
                _secondaryButtonDown = false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Error("OEM right mouse button release failed", exception);
        }
    }

    private void OnPhysicalDevices(
        (IReadOnlyList<PhysicalDeviceIdentity> Devices, HapticCapabilities? Output) notification)
    {
        lock (_gate)
        {
            _mouseAdmission = notification.Devices.Count > 0 && _client?.IsActive == true;
            if (!_mouseAdmission)
            {
                ResetUnderGate();
            }
        }
    }

    private void OnLifecycle(HandheldDeviceRuntime client, HandheldRuntimeState state)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_client, client))
            {
                _mouseAdmission = state.State is DeviceCycleState.Active or DeviceCycleState.Degraded;
                if (!_mouseAdmission)
                {
                    ResetUnderGate();
                }
            }
        }
    }

    private void DetachUnderGate()
    {
        if (_client is not null)
        {
            _client.OemControlsReceived -= OnControls;
            _client.OemEventReceived -= OnEvent;
            _client.PhysicalIdentitiesReceived -= OnPhysicalDevices;
            if (_lifecycleHandler is { } lifecycleHandler)
            {
                _client.LifecycleStateReceived -= lifecycleHandler;
            }
        }

        _client = null;
        _mouseAdmission = false;
        _lifecycleHandler = null;
        _controls.Clear();
    }

    private static bool ValidControl(OemControlDescriptor control)
    {
        return PlainText.IsIdentifier(control.ControlId)
               && Enum.IsDefined(control.DefaultActionHint)
               && control.Display.TryValidate(out _);
    }
}
