using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;

namespace WSGM.Overlay;

public partial class OverlayWindow
{
    private readonly PowerActionConfirms _powerConfirms;

    /// <summary>
    ///     Raised after the sheet is dismissed for a machine power action. The controller decides whether this
    ///     surface may act on the machine; the window never calls Windows itself.
    /// </summary>
    internal event Action<SessionPowerAction>? PowerActionRequested;

    /// <summary>The Wake and Idle timeouts editors, with the Keep Awake indicator.</summary>
    internal OverlayPowerEditors PowerEditors { get; }

    /// <summary>The UAC prompt and sign-in-after-standby switches on the System and Wake pages.</summary>
    internal WindowsPolicyRows WindowsPolicies { get; }

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
        if (!_powerConfirms.Confirm(PowerActionConfirms.Kind.CloseLauncher))
        {
            return;
        }

        _powerConfirms.Reset();
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

    /// <summary>Restart, shut down and sign out, named by the row's CommandParameter; each needs a second press.</summary>
    private void OnConfirmedPowerAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: SessionPowerAction action })
        {
            return;
        }

        var confirm = action switch
        {
            SessionPowerAction.Restart => PowerActionConfirms.Kind.Restart,
            SessionPowerAction.Shutdown => PowerActionConfirms.Kind.Shutdown,
            _ => PowerActionConfirms.Kind.SignOut
        };
        if (!_powerConfirms.Confirm(confirm))
        {
            return;
        }

        Dismissed?.Invoke();
        PowerActionRequested?.Invoke(action);
    }
}
