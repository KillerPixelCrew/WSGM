using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Interop;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     The Add Screenshot countdown over the game: a small top-center, non-activating, click-through window.
/// </summary>
/// <remarks>
///     It never takes focus or input from the game, and Windows Graphics Capture records only the game's own
///     window, so the countdown is never part of the screenshot. It is hidden before capture anyway. Over a
///     confirmed exclusive-fullscreen game it is not shown at all, since a topmost window there could knock the
///     game out of fullscreen; the countdown still runs and the overlay reports the result.
/// </remarks>
internal sealed class NotesCountdownWindow : Window
{
    private readonly TextBlock _count;
    private readonly TextBlock _detail;
    private bool _suppressed;

    /// <summary>Creates the hidden window.</summary>
    internal NotesCountdownWindow()
    {
        Width = 360;
        Height = 96;
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        _count = new TextBlock { FontSize = 30, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _detail = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        row.Children.Add(_count);
        Grid.SetColumn(_detail, 1);
        row.Children.Add(_detail);
        Border frame = new()
        {
            IsHitTestVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(20, 12),
            BorderThickness = new Thickness(1), Child = row
        };
        if (this.TryFindResource("HcOsdBackgroundBrush", out var background) && background is IBrush fill)
        {
            frame.Background = fill;
        }

        if (this.TryFindResource("HcControlBrush", out var border) && border is IBrush line)
        {
            frame.BorderBrush = line;
        }

        Content = frame;
        Opened += (_, _) => PositionAndMakeClickThrough();
        Win32Properties.AddWndProcHookCallback(this, WndProcHook);
    }

    /// <summary>Shows or updates the countdown; UI thread.</summary>
    /// <param name="remaining">Seconds left.</param>
    /// <param name="game">The game.</param>
    internal void Show(int remaining, string game)
    {
        if (!IsVisible && !_suppressed && !OverlayPopupVisibility.CanShow())
        {
            // Checked once per countdown; Hide clears it for the next one.
            _suppressed = true;
            Log.Info("Notes countdown not shown: the game holds exclusive fullscreen.");
        }

        if (_suppressed)
        {
            return;
        }

        _count.Text = remaining.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _detail.Text = $"Screenshot of {game}. Open the WSGM overlay to cancel.";
        if (!IsVisible)
        {
            base.Show();
        }
    }

    /// <summary>Hides the countdown; UI thread.</summary>
    internal new void Hide()
    {
        _suppressed = false;
        if (IsVisible)
        {
            base.Hide();
        }
    }

    private void PositionAndMakeClickThrough()
    {
        if (Screens.Primary is { } screen)
        {
            var width = (int)Math.Ceiling(Width * DesktopScaling);
            Position = new PixelPoint(screen.Bounds.X + Math.Max(0, (screen.Bounds.Width - width) / 2),
                screen.Bounds.Y + Math.Max(24, (int)Math.Round(screen.Bounds.Height * 0.06)));
        }

        var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0)
        {
            return;
        }

        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle,
            style | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent);
    }

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return NativeMethods.HtTransparent;
    }
}
