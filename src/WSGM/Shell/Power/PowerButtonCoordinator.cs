using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using OemControlDescriptor = LibHandheld.Contracts.OemControlDescriptor;
using OemControlEdge = LibHandheld.Contracts.OemControlEdge;
using OemControlEvent = LibHandheld.Contracts.OemControlEvent;
using OemControlPlacement = LibHandheld.Contracts.OemControlPlacement;

namespace WSGM.Shell;

/// <summary>
///     Handles the handheld's power button when its definition can read both edges: a short press runs
///     the configured action, a long press opens a power menu (Steam's in Game Mode, the overlay's on the
///     desktop).
/// </summary>
/// <remarks>
///     <para>
///         The device publishes a <see cref="OemControlPlacement.Power" /> control only while it is reading
///         the button. Its arrival takes Windows' own handling over (<see cref="PowerButtonTakeover" />); its
///         departure, a detached device and disposal give the button back, so a model or a moment where WSGM
///         cannot see the button leaves it to Windows. A suspend keeps the takeover, because the press that
///         wakes the machine must not run Windows' action.
///     </para>
///     <para>
///         The long press acts while the button is still held. The short press acts on release: suspending
///         with the button down made Windows offer its own slide-to-shut-down after the wake (Atlas 8,
///         2026-11-01). A press while the display is off, or within the settle time after a wake, belongs to
///         the wake and does nothing. Edges arrive on the device's reader threads; every decision is taken
///         under one lock and the actions run on the thread pool.
///     </para>
/// </remarks>
internal sealed class PowerButtonCoordinator : IDisposable
{
    /// <summary>How long a hold must last to count as a long press.</summary>
    internal static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(1000);

    /// <summary>How long after a wake or a lit display a press is still part of that wake.</summary>
    internal static readonly TimeSpan WakeSettle = TimeSpan.FromMilliseconds(1500);

    private readonly ConfigStore _store;
    private readonly Lock _gate = new();
    private readonly Timer _longTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private HandheldDeviceRuntime? _client;
    private PowerButtonServices? _services;
    private PowerButtonShortPress _shortPress = PowerButtonShortPress.Sleep;
    private string? _controlId;
    private string? _heldPress;
    private bool _longFired;
    private bool _ignoreHeld;
    private bool _displayOff;
    private long _settleUntil;
    private bool _steamResumePending;
    private bool _takenOver;
    private bool _disposed;
    private Task _work = Task.CompletedTask;

    /// <param name="store">The configuration Windows' original handling is saved to.</param>
    internal PowerButtonCoordinator(ConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _longTimer = new Timer(_ => OnLongPress(), null, Timeout.Infinite, Timeout.Infinite);

        // A takeover left by a crash is undone until this session can read the button again.
        Enqueue("restore a left-over takeover", () => PowerButtonTakeover.RestoreBestEffort(_store));
    }

    /// <summary>Completes when queued takeover, restore and action work has finished.</summary>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _work;
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
            ResetPressUnderGate();
            GiveBackUnderGate("WSGM is stopping");
        }

        _lifetime.Cancel();
        _longTimer.Dispose();
    }

    /// <summary>Supplies the session's actions; presses before this only update the held state.</summary>
    /// <param name="services">The session's action ports.</param>
    internal void ConfigureActions(PowerButtonServices services)
    {
        lock (_gate)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
        }
    }

    /// <summary>Applies the configured short-press action.</summary>
    /// <param name="shortPress">The user's choice.</param>
    internal void UpdateConfiguration(PowerButtonShortPress shortPress)
    {
        lock (_gate)
        {
            _shortPress = shortPress;
        }
    }

    /// <summary>Follows a new device cycle's power-button control and edges.</summary>
    /// <param name="client">The started device runtime; not owned.</param>
    internal void Attach(HandheldDeviceRuntime client)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DetachUnderGate();
            _client = client;
            client.OemControlsReceived += OnControls;
            client.OemEventReceived += OnEvent;
        }
    }

    /// <summary>Stops following the device and gives the button back to Windows.</summary>
    internal void Detach()
    {
        lock (_gate)
        {
            DetachUnderGate();
            ResetPressUnderGate();
            GiveBackUnderGate("the device stopped");
        }
    }

    /// <summary>Forgets a held press across a device suspend, keeping the takeover for the wake.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            ResetPressUnderGate();
        }
    }

    /// <summary>Records this session's display turning off or on.</summary>
    /// <param name="off">Whether the display is now off.</param>
    internal void NoteDisplay(bool off)
    {
        lock (_gate)
        {
            if (_displayOff && !off)
            {
                SettleUnderGate();
            }

            _displayOff = off;
        }
    }

    /// <summary>Records a system resume and tells Steam about it when its suspend screen preceded the sleep.</summary>
    internal void NoteResume()
    {
        PowerButtonServices? services;
        lock (_gate)
        {
            SettleUnderGate();
            ResetPressUnderGate();
            if (!_steamResumePending || _disposed)
            {
                return;
            }

            _steamResumePending = false;
            services = _services;
        }

        if (services is not null)
        {
            NotifySteamResumed(services);
        }
    }

    /// <summary>Whether a power-button control is published and Windows' handling is held off.</summary>
    internal bool Active
    {
        get
        {
            lock (_gate)
            {
                return _takenOver;
            }
        }
    }

    /// <summary>Raised on a worker thread when <see cref="Active" /> changes.</summary>
    internal event Action? ActiveChanged;

    private void OnControls(IReadOnlyList<OemControlDescriptor> controls)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var power = controls.Where(control => control.Placement == OemControlPlacement.Power).ToList();
            _controlId = power.Count == 1 ? power[0].ControlId : null;
            if (power.Count > 1)
            {
                Log.Warn("Power button: the device published more than one power button; none is used.");
            }

            if (_controlId is not null && !_takenOver)
            {
                _takenOver = true;
                Enqueue("take the power button over", () =>
                {
                    PowerButtonTakeover.TakeOver(_store);
                    ActiveChanged?.Invoke();
                });
            }
            else if (_controlId is null)
            {
                ResetPressUnderGate();
                GiveBackUnderGate("the device no longer reads it");
            }
        }
    }

    private void OnEvent(OemControlEvent input)
    {
        lock (_gate)
        {
            if (_disposed || _controlId is null || !string.Equals(input.ControlId, _controlId, StringComparison.Ordinal))
            {
                return;
            }

            if (input.Edge == OemControlEdge.Pressed)
            {
                if (_heldPress is not null)
                {
                    return;
                }

                _heldPress = input.DeduplicationId;
                _longFired = false;
                // A press that wakes the display, or comes right after a wake, belongs to that wake.
                _ignoreHeld = _displayOff || Environment.TickCount64 < _settleUntil;
                if (_ignoreHeld)
                {
                    Log.Info("Power button: press ignored, it is part of a wake.");
                    return;
                }

                _longTimer.Change(LongPress, Timeout.InfiniteTimeSpan);
                return;
            }

            if (!string.Equals(_heldPress, input.DeduplicationId, StringComparison.Ordinal))
            {
                return;
            }

            _longTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var acted = _longFired || _ignoreHeld;
            _heldPress = null;
            if (acted)
            {
                return;
            }

            var services = _services;
            var shortPress = _shortPress;
            if (services is null)
            {
                return;
            }

            Run("handle a short press", async token =>
            {
                // A second press closes an open power menu instead of acting underneath it.
                if (await services.CloseDesktopPowerMenuAsync(token).ConfigureAwait(false))
                {
                    Log.Info("Power button: short press closed the power menu.");
                    return true;
                }

                Log.Info($"Power button: short press, {shortPress}.");
                RunShortPress(services, shortPress);
                return true;
            });
        }
    }

    private void OnLongPress()
    {
        lock (_gate)
        {
            if (_disposed || _heldPress is null || _longFired || _ignoreHeld || _services is not { } services)
            {
                return;
            }

            _longFired = true;
            if (services.InGameMode())
            {
                Log.Info("Power button: long press, Steam power menu.");
                Run("open Steam's power menu", async token =>
                {
                    if (await services.OpenSteamPowerMenuAsync(token).ConfigureAwait(false))
                    {
                        return true;
                    }

                    Log.Warn("Power button: Steam's power menu could not be opened; showing WSGM's.");
                    return await services.ToggleDesktopPowerMenuAsync(token).ConfigureAwait(false);
                });
            }
            else
            {
                Log.Info("Power button: long press, power menu.");
                Run("open the power menu", services.ToggleDesktopPowerMenuAsync);
            }
        }
    }

    private void RunShortPress(PowerButtonServices services, PowerButtonShortPress shortPress)
    {
        switch (shortPress)
        {
            case PowerButtonShortPress.Nothing:
                return;
            case PowerButtonShortPress.Sleep:
                Standby(services);
                return;
            case PowerButtonShortPress.SteamSuspend:
                if (!services.InGameMode())
                {
                    Standby(services);
                    return;
                }

                Run("Steam suspend", async token =>
                {
                    var result = await services.RunSteamSuspendFlowAsync(token).ConfigureAwait(false);
                    if (result is SteamSuspendFlowResult.TimedOut)
                    {
                        // Steam can stay "suspending" after a failed preparation; its resume resets that.
                        NotifySteamResumed(services);
                    }
                    else if (result is SteamSuspendFlowResult.Completed)
                    {
                        lock (_gate)
                        {
                            _steamResumePending = true;
                        }
                    }
                    else
                    {
                        Log.Warn($"Power button: Steam's suspend screen did not run ({result}); sleeping directly.");
                    }

                    if (!services.EnterStandby())
                    {
                        Log.Warn("Power button: Modern Standby could not be requested.");
                    }

                    return true;
                });
                return;
            case PowerButtonShortPress.Hibernate:
                services.RequestPowerAction(SessionPowerAction.Hibernate);
                return;
            case PowerButtonShortPress.Shutdown:
                services.RequestPowerAction(SessionPowerAction.Shutdown);
                return;
            case PowerButtonShortPress.Restart:
                services.RequestPowerAction(SessionPowerAction.Restart);
                return;
        }
    }

    private static void Standby(PowerButtonServices services)
    {
        if (!services.EnterStandby())
        {
            Log.Warn("Power button: Modern Standby could not be requested.");
        }
    }

    private void NotifySteamResumed(PowerButtonServices services)
    {
        Run("tell Steam the PC resumed", async token =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(2));
            return await services.NotifySteamResumedAsync(budget.Token).ConfigureAwait(false);
        });
    }

    private void Run(string operation, Func<CancellationToken, Task<bool>> action)
    {
        var token = _lifetime.Token;
        Log.Observe(Task.Run(async () =>
        {
            if (!await action(token).ConfigureAwait(false))
            {
                Log.Warn($"Power button: could not {operation}.");
            }
        }, token), $"Power button: {operation}");
    }

    private void Enqueue(string operation, Action work)
    {
        lock (_gate)
        {
            _work = _work.ContinueWith(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Error($"Power button: could not {operation}", ex);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void GiveBackUnderGate(string reason)
    {
        _controlId = null;
        if (!_takenOver)
        {
            return;
        }

        _takenOver = false;
        Log.Info($"Power button: Windows handles it again, because {reason}.");
        Enqueue("give the power button back", () =>
        {
            PowerButtonTakeover.Restore(_store);
            ActiveChanged?.Invoke();
        });
    }

    private void SettleUnderGate()
    {
        _settleUntil = Environment.TickCount64 + (long)WakeSettle.TotalMilliseconds;
    }

    private void ResetPressUnderGate()
    {
        _heldPress = null;
        _longFired = false;
        _ignoreHeld = false;
        _longTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void DetachUnderGate()
    {
        if (_client is not { } client)
        {
            return;
        }

        client.OemControlsReceived -= OnControls;
        client.OemEventReceived -= OnEvent;
        _client = null;
    }
}
