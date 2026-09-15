using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private void OnShowWakeLockHolders(object? sender, RoutedEventArgs e)
    {
        WakeLockHost.Open();
        EnterSubView(OverlayPage.PowerWakeLocks);
    }

    private void OnCloseLauncher(object? sender, RoutedEventArgs e)
    {
        if (!_confirmCloseLauncher)
        {
            _confirmCloseLauncher = true;
            // Via the view model: the title is bound to CloseLauncherText, and a
            // direct Text write here would silently be overwritten by any
            // HomeAppName-triggered re-evaluation of that binding.
            if (DataContext is OverlayViewModel vm)
            {
                vm.ConfirmingCloseLauncher = true;
            }
            ArmConfirmReset();
            return;
        }
        ResetConfirms();
        CloseLauncherRequested?.Invoke();
    }

    private void OnStandby(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Standby();
    }

    private void OnHibernate(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Hibernate();
    }

    // Deliberately no dismiss: the row is a toggle, and the updated description/badge
    // are the immediate feedback the user is looking at.
    private void OnKeepAwakeToggle(object? sender, RoutedEventArgs e)
        => KeepAwakeToggleRequested?.Invoke();

    /// <summary>Paints the Keep Awake row's status dot in the WakeWatch color
    /// vocabulary: green free, yellow standby-blocked, red display-pinned, grey
    /// unknown. Brushes come from the palette tokens; set from the controller's
    /// indicator poll.</summary>
    /// <param name="state">The system-wide wake-lock state.</param>
    internal void SetKeepAwakeStatus(Core.WakeLockState state)
        => KeepAwakeButton.StatusBrush = this.FindResource(state switch
        {
            Core.WakeLockState.DisplayHeld => "HcDangerBrush",
            Core.WakeLockState.SystemHeld => "HcWarningBrush",
            Core.WakeLockState.Free => "HcSuccessBrush",
            _ => "HcTextMutedBrush",
        }) as Avalonia.Media.IBrush;

    /// <summary>Cycles the idle timeout a row names in its CommandParameter.</summary>
    private void OnCyclePowerTimeout(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: Core.PowerTimeoutKind kind })
        {
            PowerTimeoutCycleRequested?.Invoke(kind);
        }
    }

    private void OnRestart(object? sender, RoutedEventArgs e)
    {
        if (!_confirmRestart)
        {
            _confirmRestart = true;
            RestartButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }
        Core.PowerActions.Restart();
    }

    private void OnShutdown(object? sender, RoutedEventArgs e)
    {
        if (!_confirmShutdown)
        {
            _confirmShutdown = true;
            ShutdownButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }
        Core.PowerActions.Shutdown();
    }

    /// <summary>Armed "Really?" confirms revert on their own — after ~5 s and when
    /// the panel closes — so a stray second press minutes later cannot restart or
    /// shut down the device.</summary>
    private void ArmConfirmReset()
    {
        if (_confirmResetTimer is null)
        {
            // Parameterless ctor + explicit Start: Avalonia's 3-arg
            // DispatcherTimer ctor auto-starts, which silently defeats every
            // "start it if it isn't running" guard.
            _confirmResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _confirmResetTimer.Tick += (_, _) => ResetConfirms();
        }
        _confirmResetTimer.Stop();
        _confirmResetTimer.Start();
    }

    private void ResetConfirms()
    {
        _confirmResetTimer?.Stop();
        _confirmRestart = false;
        _confirmShutdown = false;
        _confirmCloseLauncher = false;
        if (DataContext is OverlayViewModel vm)
        {
            vm.ConfirmingCloseLauncher = false;
        }
        RestartButton.Title = "Restart";
        ShutdownButton.Title = "Shut down";
    }
}
