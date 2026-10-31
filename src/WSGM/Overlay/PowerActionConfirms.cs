using System;
using Avalonia.Threading;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>
///     The second-press confirmation of the Power page's destructive actions: the first press arms
///     an action and retitles its row "Really?", the second lets it through.
/// </summary>
/// <remarks>
///     One action is armed at a time; arming another disarms the first. An armed confirm reverts on
///     its own after about five seconds and when the panel closes, so a stray second press minutes
///     later cannot restart or shut down the device.
/// </remarks>
internal sealed class PowerActionConfirms
{
    private readonly ActionButton _restart;
    private readonly ActionButton _shutdown;
    private readonly ActionButton _signOut;
    private readonly OverlayViewModel? _viewModel;
    private Kind? _armed;
    private DispatcherTimer? _resetTimer;

    /// <summary>Creates the confirms over the rows they retitle.</summary>
    /// <param name="viewModel">
    ///     The sheet's state, or null. Close-launcher's title is bound to its CloseLauncherText, so it
    ///     is armed through the view model rather than by writing the row's title.
    /// </param>
    /// <param name="restart">The Restart row.</param>
    /// <param name="shutdown">The Shut down row.</param>
    /// <param name="signOut">The Sign out row.</param>
    internal PowerActionConfirms(OverlayViewModel? viewModel, ActionButton restart, ActionButton shutdown,
        ActionButton signOut)
    {
        _viewModel = viewModel;
        _restart = restart;
        _shutdown = shutdown;
        _signOut = signOut;
    }

    /// <summary>The actions that ask for a second press.</summary>
    internal enum Kind
    {
        CloseLauncher,
        Restart,
        Shutdown,
        SignOut
    }

    /// <summary>Whether an action is waiting for its second press.</summary>
    internal bool IsArmed => _armed is not null;

    /// <summary>Arms an action on its first press and lets the second press through.</summary>
    /// <param name="action">The pressed action.</param>
    /// <returns>True on the confirming second press; false when this press only armed it.</returns>
    internal bool Confirm(Kind action)
    {
        if (_armed == action)
        {
            return true;
        }

        _armed = action;
        Show();
        if (_resetTimer is null)
        {
            // Parameterless ctor + explicit Start: Avalonia's 3-arg
            // DispatcherTimer ctor auto-starts, which silently defeats every
            // "start it if it isn't running" guard.
            _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _resetTimer.Tick += (_, _) => Reset();
        }

        _resetTimer.Stop();
        _resetTimer.Start();
        return false;
    }

    /// <summary>Disarms every action and restores the rows' titles.</summary>
    internal void Reset()
    {
        _resetTimer?.Stop();
        _armed = null;
        Show();
    }

    private void Show()
    {
        if (_viewModel is not null)
        {
            _viewModel.ConfirmingCloseLauncher = _armed == Kind.CloseLauncher;
        }

        _restart.Title = _armed == Kind.Restart ? "Really?" : "Restart";
        _shutdown.Title = _armed == Kind.Shutdown ? "Really?" : "Shut down";
        _signOut.Title = _armed == Kind.SignOut ? "Really?" : "Sign out";
    }
}
