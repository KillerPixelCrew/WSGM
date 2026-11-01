using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Controls;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>Power > Power: standby, hibernate, restart, shut down and sign out, and the centred power menu.</summary>
/// <remarks>
///     Restart, shut down and sign out ask for a second press through the sheet's
///     <see cref="PowerActionConfirms" />. Every action asks the sheet to close before it is reported, and the
///     controller decides whether this surface may act on the machine; the page never calls Windows itself.
/// </remarks>
public sealed partial class PowerActionsPage : StackPanel
{
    private PowerActionConfirms? _confirms;

    /// <summary>Builds the rows; the window supplies the confirmation with <see cref="Configure" />.</summary>
    public PowerActionsPage()
    {
        InitializeComponent();
    }

    /// <summary>The rows whose action asks for a second press, which the power menu marks too.</summary>
    internal IReadOnlyCollection<ActionButton> ConfirmedActions => [RestartButton, SignOutButton];

    /// <summary>Raised before a power action is reported, so the sheet closes first.</summary>
    internal event Action? DismissRequested;

    /// <summary>Raised with a machine power action once it is confirmed.</summary>
    internal event Action<SessionPowerAction>? PowerActionRequested;

    /// <summary>Raised when the user opens the centred power menu.</summary>
    internal event Action? PowerMenuRequested;

    /// <summary>Supplies the second-press confirmation of the destructive actions.</summary>
    /// <param name="confirms">The sheet's confirmations.</param>
    internal void Configure(PowerActionConfirms confirms)
    {
        _confirms = confirms;
    }

    /// <summary>The page's visible action rows in the power menu's order.</summary>
    /// <param name="desktop">The Session page's desktop row, which the menu offers too while it shows.</param>
    /// <returns>The rows the menu projects; new Power actions are included automatically.</returns>
    internal IReadOnlyList<ActionButton> MenuActions(ActionButton desktop)
    {
        var sources = Children.OfType<ActionButton>().Where(source => source.IsVisible).ToList();
        // Placement differs from the linear page.
        if (sources.Remove(SignOutButton))
        {
            var shutdown = sources.IndexOf(ShutdownButton);
            sources.Insert(shutdown >= 0 ? shutdown : sources.Count, SignOutButton);
        }

        if (desktop.IsVisible)
        {
            sources.Insert(Math.Min(2, sources.Count), desktop);
        }

        return sources;
    }

    private void OnOpenPowerMenu(object? sender, RoutedEventArgs e)
    {
        PowerMenuRequested?.Invoke();
    }

    private void OnStandby(object? sender, RoutedEventArgs e)
    {
        Report(SessionPowerAction.Standby);
    }

    private void OnHibernate(object? sender, RoutedEventArgs e)
    {
        Report(SessionPowerAction.Hibernate);
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
        if (_confirms is null || !_confirms.Confirm(confirm))
        {
            return;
        }

        Report(action);
    }

    private void Report(SessionPowerAction action)
    {
        DismissRequested?.Invoke();
        PowerActionRequested?.Invoke(action);
    }
}
