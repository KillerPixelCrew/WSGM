using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Windows power state the open sheet shows and changes: Keep awake and its wake-lock indicator, the
///     idle timeouts and the UAC prompt and sign-in-after-standby policies.
/// </summary>
/// <remarks>
///     The session's owners are borrowed, so writes share their scheme lock; a preview reads Windows but never
///     changes it. Reads queue behind each other on a worker, so opening the sheet never waits on the power or
///     registry APIs; each read posts its result in request order, and only to the sheet it was read for. Use
///     on the UI thread.
/// </remarks>
internal sealed class OverlayPowerState : IDisposable
{
    private readonly DisplayTimeouts? _displayTimeouts;
    private readonly KeepAwakeService? _keepAwake;
    private readonly PowerButtonSetting? _powerButton;
    private readonly WindowsPowerPolicy _power;
    private readonly bool _previewOnly;
    private bool _disposed;
    private string? _lastWakeLockError;
    private OverlayWindow? _overlay;
    private Task _powerTimeoutWrite = Task.CompletedTask;
    private OverlayViewModel? _viewModel;
    private bool _wakeLockQueryRunning;
    private DispatcherTimer? _wakeLockRefresh;
    private Task _windowsReads = Task.CompletedTask;

    /// <summary>Follows the session's Keep awake and display timeout owners.</summary>
    /// <param name="power">The Windows power policy owners the timeout rows read and write.</param>
    /// <param name="keepAwake">The session's Keep awake owner, or null on a surface without one.</param>
    /// <param name="displayTimeouts">The display timeouts shared with Steam's Screensaver settings, or null.</param>
    /// <param name="previewOnly">Whether this surface is a preview that never changes the machine.</param>
    /// <param name="powerButton">The power button's short-press choice shared with Steam, or null.</param>
    internal OverlayPowerState(WindowsPowerPolicy power, KeepAwakeService? keepAwake,
        DisplayTimeouts? displayTimeouts, bool previewOnly, PowerButtonSetting? powerButton = null)
    {
        _powerButton = powerButton;
        if (_powerButton is not null)
        {
            _powerButton.Changed += OnPowerButtonChanged;
        }

        _power = power;
        _keepAwake = keepAwake;
        _displayTimeouts = displayTimeouts;
        _previewOnly = previewOnly;
        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed += OnDisplayTimeoutsChanged;
        }

        if (_keepAwake is not null)
        {
            _keepAwake.StateChanged += OnKeepAwakeStateChanged;
        }
    }

    /// <summary>Stops following the session's owners; the services themselves belong to the session.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_powerButton is not null)
        {
            _powerButton.Changed -= OnPowerButtonChanged;
        }

        if (_displayTimeouts is not null)
        {
            _displayTimeouts.Changed -= OnDisplayTimeoutsChanged;
        }

        if (_keepAwake is not null)
        {
            _keepAwake.StateChanged -= OnKeepAwakeStateChanged;
        }
    }

    /// <summary>Connects a newly opened sheet's power editors and policy switches to the session.</summary>
    /// <param name="overlay">The sheet.</param>
    /// <param name="vm">Its view model.</param>
    internal void Attach(OverlayWindow overlay, OverlayViewModel vm)
    {
        _overlay = overlay;
        _viewModel = vm;
        overlay.PowerEditors.KeepAwakeSelected += mode =>
        {
            _keepAwake?.SetManualMode(mode);
            // Also reconcile a refused request whose actual mode did not change:
            // the view model then emits no property notification of its own.
            vm.KeepAwakeManualMode = _keepAwake?.ManualMode ?? ManualWakeMode.Off;
            overlay.PowerEditors.Refresh();
        };
        overlay.PowerEditors.PowerButtonSelected += async value =>
        {
            if (_previewOnly || _powerButton is null)
            {
                return;
            }

            try
            {
                await _powerButton.SelectAsync(value, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("The power button choice could not be saved", ex);
            }

            if (ReferenceEquals(_viewModel, vm))
            {
                RefreshPowerButton(vm);
                overlay.PowerEditors.Refresh();
            }
        };
        overlay.PowerEditors.PowerTimeoutSelected += async (kind, seconds) =>
        {
            if (_previewOnly)
            {
                return;
            }

            // Scheme writes share a gate with the profile selector. Waiting for it must not
            // block input. Explicit selections do not require a prior read.
            // Enqueue on the UI thread, before yielding, to preserve explicit choice order
            // across rapid selections and close/reopen. A failed predecessor is not retried.
            var write = _powerTimeoutWrite.ContinueWith(_ =>
            {
                // The session's owner skips display presets Steam's screensaver forbids and tells
                // Steam's Screensaver settings about the change.
                return _displayTimeouts?.Select(kind, seconds) == true;
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _powerTimeoutWrite = write;
            try
            {
                var written = await write;
                if (written && ReferenceEquals(_viewModel, vm))
                {
                    PublishPowerTimeouts(vm, _displayTimeouts?.ObservedValues
                                             ?? new Dictionary<PowerTimeoutKind, int?> { [kind] = seconds });
                }
            }
            catch (Exception ex)
            {
                Log.Error("The selected power timeout could not be applied", ex);
            }
        };
        // Off the UI thread: the elevated one-shot blocks for as long as its consent prompt is
        // on screen, and a frozen sheet holding the Steam Input lease reads as a hang.
        overlay.WindowsPolicies.UacPromptsRequested += async disable =>
        {
            try
            {
                await Task.Run(() => UacSettings.RequestChange(disable));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Could not change UAC prompts", ex);
            }

            RefreshWindowsPolicies(overlay);
        };
        overlay.WindowsPolicies.LockOnWakeRequested += async disable =>
        {
            try
            {
                await Task.Run(() => LockScreenSettings.RequestChange(disable));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Error("Could not change lock on wake", ex);
            }

            RefreshWindowsPolicies(overlay);
        };
    }

    /// <summary>Forgets the closed sheet and stops its wake-lock indicator.</summary>
    internal void Detach()
    {
        _overlay = null;
        _viewModel = null;
        _wakeLockRefresh?.Stop();
    }

    /// <summary>Copies Keep awake and the idle timeouts into an open or re-summoned sheet's view model.</summary>
    /// <param name="vm">The sheet's view model.</param>
    internal void Refresh(OverlayViewModel vm)
    {
        vm.KeepAwakeManualMode = _keepAwake?.ManualMode ?? ManualWakeMode.Off;
        vm.KeepAwakeDownloadActive = _keepAwake?.DownloadHold ?? false;
        RefreshPowerButton(vm);
        RefreshPowerTimeouts(vm);
    }

    private void RefreshPowerButton(OverlayViewModel vm)
    {
        vm.PowerButtonAvailable = _powerButton?.Available == true;
        vm.PowerButtonShortPress = _powerButton?.Current ?? PowerButtonShortPress.Sleep;
    }

    private void OnPowerButtonChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && _viewModel is { } vm)
            {
                RefreshPowerButton(vm);
            }
        });
    }

    /// <summary>Reads the idle timeouts again once the Windows energy plan changed and settled.</summary>
    /// <param name="schemes">The sheet's energy plan selection.</param>
    /// <param name="vm">The sheet's view model.</param>
    internal void FollowSchemes(ChoiceRowSelection schemes, OverlayViewModel vm)
    {
        schemes.Changed += () =>
        {
            if (schemes is { Busy: false, State.Current.Length: > 0 } && ReferenceEquals(_viewModel, vm))
            {
                RefreshPowerTimeouts(vm);
            }
        };
    }

    /// <summary>
    ///     Shows the UAC prompt and wake sign-in policies as Windows reports them. A preview
    ///     surface shows them read-only: it must not change the machine. Until the first answer
    ///     lands the switches stay disabled, as the window creates them.
    /// </summary>
    /// <param name="overlay">The sheet the answer is for.</param>
    internal void RefreshWindowsPolicies(OverlayWindow overlay)
    {
        QueueWindowsRead(() =>
        {
            bool uacPromptsDisabled;
            bool lockOnWakeDisabled;
            try
            {
                uacPromptsDisabled = UacSettings.Read().PromptsDisabled;
                lockOnWakeDisabled = LockScreenSettings.SignInOnWakeDisabled();
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the Windows sign-in policies", ex);
                return null;
            }

            return () =>
            {
                if (!_disposed && ReferenceEquals(_overlay, overlay))
                {
                    overlay.WindowsPolicies.Refresh(uacPromptsDisabled, lockOnWakeDisabled, !_previewOnly);
                }
            };
        });
    }

    /// <summary>
    ///     Polls the system-wide power-request list into the Keep Awake row's
    ///     WakeWatch-style dot while the Wake page shows (~65 µs syscall, WakeWatch runs
    ///     it at 1 Hz permanently). Started per open, stopped with the panel.
    /// </summary>
    internal void StartWakeLockRefresh()
    {
        RefreshWakeLockIndicator();
        if (_keepAwake is null)
        {
            return;
        }

        if (_wakeLockRefresh is null)
        {
            // Parameterless ctor + explicit Start (the 3-arg ctor auto-starts and
            // defeats IsEnabled guards — device-verified invariant).
            _wakeLockRefresh = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            // Only the Wake page shows the indicator, so a tick while it is hidden reads nothing.
            _wakeLockRefresh.Tick += (_, _) =>
            {
                if (_overlay?.PowerEditors.KeepAwakeShowing == true)
                {
                    RefreshWakeLockIndicator();
                }
            };
        }

        _wakeLockRefresh.Start();
    }

    /// <summary>
    ///     Reads the idle timeouts from the active power scheme into the Power tab's editors, with the
    ///     bounds Steam's screensaver sets on the display timeouts.
    /// </summary>
    private void RefreshPowerTimeouts(OverlayViewModel vm)
    {
        QueueWindowsRead(() =>
        {
            IReadOnlyDictionary<PowerTimeoutKind, int?> timeouts;
            try
            {
                timeouts = _power.Timeouts.ReadAll();
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the idle timeouts", ex);
                timeouts = new Dictionary<PowerTimeoutKind, int?>();
            }

            return () => ApplyPowerTimeouts(vm, timeouts);
        });
    }

    private void ApplyPowerTimeouts(OverlayViewModel vm,
        IReadOnlyDictionary<PowerTimeoutKind, int?> timeouts)
    {
        if (_disposed || !ReferenceEquals(_viewModel, vm))
        {
            return;
        }

        // Every kind is published, so a value the read did not return replaces the old one with null.
        PublishPowerTimeouts(vm, Enum.GetValues<PowerTimeoutKind>().ToDictionary(kind => kind,
            kind => timeouts.GetValueOrDefault(kind)));
        _displayTimeouts?.Observe(vm.PowerTimeoutValues);
    }

    /// <summary>Runs a read behind the previous one on a worker and posts its result to the UI thread.</summary>
    /// <param name="read">The worker step; it returns the UI step, or null when there is nothing to show.</param>
    private void QueueWindowsRead(Func<Action?> read)
    {
        _windowsReads = _windowsReads.ContinueWith(_ =>
        {
            if (read() is { } apply)
            {
                Dispatcher.UIThread.Post(apply);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    ///     Mirrors keep-awake hold changes (poll loop or toggle, any thread)
    ///     into an open panel's view model.
    /// </summary>
    private void OnKeepAwakeStateChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _viewModel is null || _keepAwake is null)
            {
                return;
            }

            _viewModel.KeepAwakeManualMode = _keepAwake.ManualMode;
            _viewModel.KeepAwakeDownloadActive = _keepAwake.DownloadHold;
        });
    }

    /// <summary>
    ///     Mirrors a display timeout chosen in Steam's Screensaver settings, or a new bound from
    ///     Steam's screensaver timeout, into an open panel's view model.
    /// </summary>
    private void OnDisplayTimeoutsChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && _viewModel is { } vm)
            {
                PublishPowerTimeouts(vm, _displayTimeouts!.ObservedValues);
            }
        });
    }

    private void PublishPowerTimeouts(OverlayViewModel vm, IReadOnlyDictionary<PowerTimeoutKind, int?> observed)
    {
        var values = new Dictionary<PowerTimeoutKind, int?>(vm.PowerTimeoutValues);
        foreach (var (kind, seconds) in observed)
        {
            values[kind] = seconds;
        }

        // The minimums do not notify: they are set first, and the values' change refreshes the editors.
        vm.PowerTimeoutMinimums = Enum.GetValues<PowerTimeoutKind>().ToDictionary(kind => kind,
            kind => _displayTimeouts?.Minimum(kind));
        vm.PowerTimeoutValues = values;
    }

    private void RefreshWakeLockIndicator()
    {
        _ = RefreshWakeLockIndicatorAsync();
    }

    /// <summary>
    ///     Queries the power-request list on the pool and applies it on the UI thread. A tick
    ///     that arrives while a query is still running is skipped rather than queued.
    /// </summary>
    private async Task RefreshWakeLockIndicatorAsync()
    {
        if (_wakeLockQueryRunning || _disposed || _overlay is null || _viewModel is null || _keepAwake is null)
        {
            return;
        }

        _wakeLockQueryRunning = true;
        try
        {
            var (entries, status, nativeStatus) = await Task.Run(PowerRequestList.Query);
            var error = WakeLockStatus.DescribeQueryStatus(status, nativeStatus);
            if (_disposed || _overlay is not { } overlay || _viewModel is not { } viewModel)
            {
                return;
            }

            if (error != _lastWakeLockError)
            {
                // Log transitions only — this ticks every 1.5 s while the panel is open.
                _lastWakeLockError = error;
                if (error is not null)
                {
                    Log.Warn($"Wake lock indicator unavailable: {error}.");
                }
            }

            var (state, summary) = WakeLockStatus.Compute(
                entries, (uint)Environment.ProcessId);
            viewModel.WakeLockSummary = summary;
            overlay.PowerEditors.SetKeepAwakeStatus(state);
        }
        finally
        {
            _wakeLockQueryRunning = false;
        }
    }
}
