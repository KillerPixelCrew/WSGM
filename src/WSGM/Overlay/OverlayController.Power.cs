using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Overlay;

public sealed partial class OverlayController
{
    // Idle-timeout and policy reads queue behind each other on a worker, so opening the sheet never waits
    // on the power or registry APIs. Each read posts its result, and chained reads post in request order,
    // so an older answer never replaces a newer one. The values fill in a frame later.
    private Task _windowsReads = Task.CompletedTask;

    /// <summary>
    ///     Reads the four idle timeouts from the active power scheme into the
    ///     Power tab's badges ("—" when the power API gives no answer), and says when Steam's
    ///     screensaver holds a display timeout up.
    /// </summary>
    private void RefreshPowerTimeouts(OverlayViewModel vm)
    {
        QueueWindowsRead(() =>
        {
            (int? DisplayDc, int? DisplayAc, int? SleepDc, int? SleepAc) timeouts;
            try
            {
                timeouts = _power.Timeouts.ReadAll();
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the idle timeouts", ex);
                timeouts = (null, null, null, null);
            }

            return () => ApplyPowerTimeouts(vm, timeouts);
        });
    }

    private void ApplyPowerTimeouts(OverlayViewModel vm,
        (int? DisplayDc, int? DisplayAc, int? SleepDc, int? SleepAc) timeouts)
    {
        if (_disposed || !ReferenceEquals(_overlayViewModel, vm))
        {
            return;
        }

        vm.DisplayDcTimeout = Format(timeouts.DisplayDc);
        vm.DisplayAcTimeout = Format(timeouts.DisplayAc);
        vm.DisplayDcDescription = DisplayTimeoutDescription(PowerTimeoutKind.DisplayDc);
        vm.DisplayAcDescription = DisplayTimeoutDescription(PowerTimeoutKind.DisplayAc);
        vm.SleepDcTimeout = Format(timeouts.SleepDc);
        vm.SleepAcTimeout = Format(timeouts.SleepAc);
        vm.PowerTimeoutMinimums = Enum.GetValues<PowerTimeoutKind>().ToDictionary(kind => kind,
            kind => _displayTimeouts?.Minimum(kind));
        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?>
        {
            [PowerTimeoutKind.DisplayDc] = timeouts.DisplayDc,
            [PowerTimeoutKind.DisplayAc] = timeouts.DisplayAc,
            [PowerTimeoutKind.SleepDc] = timeouts.SleepDc,
            [PowerTimeoutKind.SleepAc] = timeouts.SleepAc
        };
        _displayTimeouts?.Observe(vm.PowerTimeoutValues);
        return;

        static string Format(int? seconds)
        {
            return seconds is null ? "—" : PowerTimeouts.Describe(seconds.Value);
        }
    }

    /// <summary>
    ///     Shows the UAC prompt and wake sign-in policies as Windows reports them. A preview
    ///     surface shows them read-only: it must not change the machine. Until the first answer
    ///     lands the switches stay disabled, as the window creates them.
    /// </summary>
    private void RefreshWindowsPolicies(OverlayWindow overlay)
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
                    overlay.RefreshWindowsPolicies(uacPromptsDisabled, lockOnWakeDisabled, !_previewOnly);
                }
            };
        });
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
            if (_disposed || _overlayViewModel is null || _keepAwake is null)
            {
                return;
            }

            _overlayViewModel.KeepAwakeManualMode = _keepAwake.ManualMode;
            _overlayViewModel.KeepAwakeDownloadActive = _keepAwake.DownloadHold;
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
            if (!_disposed && _overlayViewModel is { } vm)
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
            var label = seconds is { } value ? PowerTimeouts.Describe(value) : "—";
            switch (kind)
            {
                case PowerTimeoutKind.DisplayDc: vm.DisplayDcTimeout = label; break;
                case PowerTimeoutKind.DisplayAc: vm.DisplayAcTimeout = label; break;
                case PowerTimeoutKind.SleepDc: vm.SleepDcTimeout = label; break;
                case PowerTimeoutKind.SleepAc: vm.SleepAcTimeout = label; break;
            }
        }

        vm.DisplayDcDescription = DisplayTimeoutDescription(PowerTimeoutKind.DisplayDc);
        vm.DisplayAcDescription = DisplayTimeoutDescription(PowerTimeoutKind.DisplayAc);
        // The minimums do not notify: they are set first, and the values' change refreshes the editors.
        vm.PowerTimeoutMinimums = Enum.GetValues<PowerTimeoutKind>().ToDictionary(kind => kind,
            kind => _displayTimeouts?.Minimum(kind));
        vm.PowerTimeoutValues = values;
    }

    /// <summary>The display row's description: the plain one, or the bound Steam's screensaver sets.</summary>
    private string DisplayTimeoutDescription(PowerTimeoutKind kind)
    {
        return _displayTimeouts?.Minimum(kind) is { } minimum
            ? $"Idle time before the display turns off; at least {PowerTimeouts.Describe(minimum)} for Steam's screensaver"
            : OverlayViewModel.DisplayTimeoutDescription;
    }

    /// <summary>
    ///     Polls the system-wide power-request list into the Keep Awake row's
    ///     WakeWatch-style dot while the panel is open (~65 µs syscall, WakeWatch runs
    ///     it at 1 Hz permanently). Started per ShowOverlay, stopped with the panel.
    /// </summary>
    private void StartWakeLockRefresh()
    {
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
            _wakeLockRefresh.Tick += (_, _) => RefreshWakeLockIndicator();
        }

        _wakeLockRefresh.Start();
    }

    private void StopWakeLockRefresh()
    {
        _wakeLockRefresh?.Stop();
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
        if (_wakeLockQueryRunning || _disposed || _overlay is null || _overlayViewModel is null || _keepAwake is null)
        {
            return;
        }

        _wakeLockQueryRunning = true;
        try
        {
            var (entries, status, nativeStatus) = await Task.Run(PowerRequestList.Query);
            var error = WakeLockStatus.DescribeQueryStatus(status, nativeStatus);
            if (_disposed || _overlay is not { } overlay || _overlayViewModel is not { } viewModel)
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
            overlay.SetKeepAwakeStatus(state);
        }
        finally
        {
            _wakeLockQueryRunning = false;
        }
    }

    /// <summary>
    ///     Opens the power menu on the current overlay, or from the desktop without a Steam lease.
    ///     Hardware-button capture is owned by the session's input integration.
    /// </summary>
    public void ShowPowerMenu()
    {
        if (_disposed)
        {
            return;
        }

        var standalone = _overlay is null;
        ShowOverlayCore(!ExplorerControl.IsDesktopShellRunning());
        _powerMenuOnly |= standalone;
        _overlay?.ShowPowerMenu();
    }

    /// <summary>Handles a repeated power-menu request as cancellation.</summary>
    public void TogglePowerMenu()
    {
        if (PowerMenuOpen)
        {
            _overlay?.CloseActiveSurface();
            return;
        }

        ShowPowerMenu();
    }
}
