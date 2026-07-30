using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenFSE.Overlay;

public partial class OverlayWindow : Window
{
    private bool _confirmRestart;
    private bool _confirmShutdown;

    public event Action? HomeAppRequested;
    public event Action? DesktopRequested;
    public event Action? Dismissed;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        KeyDown += OnKeyDown;
        Opened += (_, _) => HomeAppButton.Focus(NavigationMethod.Directional);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismissed?.Invoke();
        }
    }

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only dismiss when the press hit the backdrop itself, not a button.
        if (ReferenceEquals(e.Source, sender))
        {
            Dismissed?.Invoke();
        }
    }

    private void OnHomeApp(object? sender, RoutedEventArgs e) => HomeAppRequested?.Invoke();
    private void OnDesktop(object? sender, RoutedEventArgs e) => DesktopRequested?.Invoke();
    private void OnClose(object? sender, RoutedEventArgs e) => Dismissed?.Invoke();

    private void OnSleep(object? sender, RoutedEventArgs e)
    {
        Dismissed?.Invoke();
        Core.PowerActions.Sleep();
    }

    private void OnRestart(object? sender, RoutedEventArgs e)
    {
        if (!_confirmRestart)
        {
            _confirmRestart = true;
            RestartButton.Content = "Really?";
            return;
        }
        Core.PowerActions.Restart();
    }

    private void OnShutdown(object? sender, RoutedEventArgs e)
    {
        if (!_confirmShutdown)
        {
            _confirmShutdown = true;
            ShutdownButton.Content = "Really?";
            return;
        }
        Core.PowerActions.Shutdown();
    }
}
