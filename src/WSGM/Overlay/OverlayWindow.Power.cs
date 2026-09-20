using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private bool _confirmSignOut;

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
        PowerActions.Standby();
    }

    private void OnHibernate(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        PowerActions.Hibernate();
    }

    /// <summary>
    ///     Paints the Keep Awake row's status dot in the WakeWatch color
    ///     vocabulary: green free, yellow standby-blocked, red display-pinned, grey
    ///     unknown. Brushes come from the palette tokens; set from the controller's
    ///     indicator poll.
    /// </summary>
    /// <param name="state">The system-wide wake-lock state.</param>
    internal void SetKeepAwakeStatus(WakeLockState state)
    {
        KeepAwakeDetail.Foreground = this.FindResource(state switch
        {
            WakeLockState.DisplayHeld => "HcDangerBrush",
            WakeLockState.SystemHeld => "HcWarningBrush",
            WakeLockState.Free => "HcSuccessBrush",
            _ => "HcTextMutedBrush"
        }) as IBrush;
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

        Dismissed?.Invoke();
        PowerActions.Restart();
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

        Dismissed?.Invoke();
        PowerActions.Shutdown();
    }

    private void OnSignOut(object? sender, RoutedEventArgs e)
    {
        if (!_confirmSignOut)
        {
            _confirmSignOut = true;
            SignOutButton.Title = "Really?";
            ArmConfirmReset();
            return;
        }

        Dismissed?.Invoke();
        PowerActions.SignOut();
    }

    /// <summary>
    ///     Armed "Really?" confirms revert on their own — after ~5 s and when
    ///     the panel closes — so a stray second press minutes later cannot restart or
    ///     shut down the device.
    /// </summary>
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
        _confirmSignOut = false;
        _confirmCloseLauncher = false;
        if (DataContext is OverlayViewModel vm)
        {
            vm.ConfirmingCloseLauncher = false;
        }

        RestartButton.Title = "Restart";
        ShutdownButton.Title = "Shut down";
        SignOutButton.Title = "Sign out";
    }
}
