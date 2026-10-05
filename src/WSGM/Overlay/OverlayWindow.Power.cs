using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private PowerConfirm? _armedConfirm;

    /// <summary>
    ///     Raised after the sheet is dismissed for a machine power action. The controller decides whether this
    ///     surface may act on the machine; the window never calls Windows itself.
    /// </summary>
    internal event Action<SessionPowerAction>? PowerActionRequested;

    private void OnShowWakeLockHolders(object? sender, RoutedEventArgs e)
    {
        WakeLockHost.Open();
        EnterSubView(OverlayPage.PowerWakeLocks);
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e)
    {
        HomeAppRequested?.Invoke();
    }

    private void OnDesktop(object? sender, RoutedEventArgs e)
    {
        DesktopRequested?.Invoke();
    }

    private void OnExitBigPicture(object? sender, RoutedEventArgs e)
    {
        ExitBigPictureRequested?.Invoke();
    }

    private void OnCloseLauncher(object? sender, RoutedEventArgs e)
    {
        if (!Confirm(PowerConfirm.CloseLauncher))
        {
            return;
        }

        ResetConfirms();
        CloseLauncherRequested?.Invoke();
    }

    private void OnStandby(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        PowerActionRequested?.Invoke(SessionPowerAction.Standby);
    }

    private void OnHibernate(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        PowerActionRequested?.Invoke(SessionPowerAction.Hibernate);
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
        if (!Confirm(PowerConfirm.Restart))
        {
            return;
        }

        Dismissed?.Invoke();
        PowerActionRequested?.Invoke(SessionPowerAction.Restart);
    }

    private void OnShutdown(object? sender, RoutedEventArgs e)
    {
        if (!Confirm(PowerConfirm.Shutdown))
        {
            return;
        }

        Dismissed?.Invoke();
        PowerActionRequested?.Invoke(SessionPowerAction.Shutdown);
    }

    private void OnSignOut(object? sender, RoutedEventArgs e)
    {
        if (!Confirm(PowerConfirm.SignOut))
        {
            return;
        }

        Dismissed?.Invoke();
        PowerActionRequested?.Invoke(SessionPowerAction.SignOut);
    }

    /// <summary>Arms an action on its first press and lets the second press through.</summary>
    /// <remarks>One action is armed at a time; arming another disarms the first.</remarks>
    private bool Confirm(PowerConfirm action)
    {
        if (_armedConfirm == action)
        {
            return true;
        }

        _armedConfirm = action;
        ShowConfirms();
        ArmConfirmReset();
        return false;
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
        _armedConfirm = null;
        ShowConfirms();
    }

    private void ShowConfirms()
    {
        // Close-launcher goes through the view model: its title is bound to CloseLauncherText, and a
        // direct Text write here would be overwritten by any HomeAppName-triggered re-evaluation.
        if (DataContext is OverlayViewModel vm)
        {
            vm.ConfirmingCloseLauncher = _armedConfirm == PowerConfirm.CloseLauncher;
        }

        RestartButton.Title = _armedConfirm == PowerConfirm.Restart ? "Really?" : "Restart";
        ShutdownButton.Title = _armedConfirm == PowerConfirm.Shutdown ? "Really?" : "Shut down";
        SignOutButton.Title = _armedConfirm == PowerConfirm.SignOut ? "Really?" : "Sign out";
    }

    private enum PowerConfirm
    {
        CloseLauncher,
        Restart,
        Shutdown,
        SignOut
    }
}
