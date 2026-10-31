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
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using PhysicalDeviceIdentity = LibHandheld.Contracts.PhysicalDeviceIdentity;

namespace WSGM.Shell;

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
    private CanonicalButtons _keyboardSteamButtons;
    private DateTimeOffset _keyboardSteamButtonsAt;
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
        bool keyboardSteamAction;
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

            var wrapperLease = _actions?.WrapperInputLeaseActive() == true;
            action = ResolveActionUnderGate(control, wrapperLease);
            keyboardSteamAction = wrapperLease
                                  && action is OemAction.ToggleSteamOverlay or OemAction.ToggleSteamQuickAccess;
            if (!OemActionRules.IsAssignable(action, control.Placement)
                || !OemActionRules.IsAvailable(action, _targetHasRearButtons)
                || (control.RequiresControllerAcquisition && !_controllerManagementEnabled))
            {
                Log.Warn($"Device OEM action unavailable: control={control.ControlId}, action={action}.");
                return;
            }

            if (keyboardSteamAction)
            {
                _keyboardSteamButtons |= control.ControllerButton;
                _keyboardSteamButtonsAt = input.Timestamp;
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

            dispatch = Task.Run(() => DispatchAsync(actions, action, input, keyboardSteamAction, cancellationToken));
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
        bool keyboardSteamAction,
        CancellationToken cancellationToken)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(3));
            var completed = keyboardSteamAction
                ? await actions.SendSteamShortcutAsync(action == OemAction.ToggleSteamQuickAccess, bounded.Token)
                    .ConfigureAwait(false)
                : action switch
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

    private OemAction ResolveActionUnderGate(OemControlDescriptor control, bool wrapperLeaseActive)
    {
        var assignment = _assignments.FirstOrDefault(item =>
            string.Equals(item.ControlId, control.ControlId, StringComparison.Ordinal));

        // Steam normally reads Guide/QAM from the target. A wrapper lease blocks that channel,
        // so the same OEM press needs the keyboard route. Explicit assignments still win.
        return assignment?.Action ?? DefaultAction(control, wrapperLeaseActive);
    }

    /// <summary>What an unassigned control does.</summary>
    /// <param name="control">Published OEM control declaration, including placement and companion-application role.</param>
    /// <param name="wrapperLeaseActive">Whether a game wrapper currently blocks Steam's controller reads.</param>
    /// <returns>Steam for leased Guide/QAM controls, right mouse for the touchpad, WSGM for a companion, otherwise disabled.</returns>
    internal static OemAction DefaultAction(OemControlDescriptor control, bool wrapperLeaseActive = false)
    {
        if (wrapperLeaseActive && control.Placement == OemControlPlacement.Front)
        {
            switch (control.ControllerButton)
            {
                case CanonicalButtons.Guide:
                    return OemAction.ToggleSteamOverlay;
                case CanonicalButtons.QuickAccess:
                    return OemAction.ToggleSteamQuickAccess;
            }
        }

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
        _keyboardSteamButtons = CanonicalButtons.None;
        _keyboardSteamButtonsAt = default;
        _heldMouseControls.Clear();
        ReleaseSecondaryButtonUnderGate();
        _recentEvents.Clear();
    }

    /// <summary>Withholds a translated OEM press from the virtual pad until its physical release.</summary>
    /// <remarks>Pure button state on the sample path; marker lookup happens only on OEM edges.</remarks>
    internal CanonicalButtons FilterControllerButtons(LibHandheld.Contracts.CanonicalControllerSample sample)
    {
        lock (_gate)
        {
            if (sample.Timestamp >= _keyboardSteamButtonsAt)
            {
                _keyboardSteamButtons &= sample.Buttons;
            }

            return sample.Buttons & ~_keyboardSteamButtons;
        }
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
               && control.ControllerButton is CanonicalButtons.None or CanonicalButtons.Guide
                   or CanonicalButtons.QuickAccess
               && control.Display.TryValidate(out _);
    }
}
