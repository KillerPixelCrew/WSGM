using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Overlay;

/// <summary>Tools > System: WSGM Settings, Task Manager and the UAC prompt switch.</summary>
public sealed partial class SystemToolsPage : StackPanel
{
    /// <summary>Builds the rows; the UAC switch is added to <see cref="UacPolicyHost" /> by its owner.</summary>
    public SystemToolsPage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user requests the Settings window.</summary>
    internal event Action? SettingsRequested;

    /// <summary>Raised when the user requests Task Manager.</summary>
    internal event Action? TaskManagerRequested;

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke();
    }

    private void OnTaskManager(object? sender, RoutedEventArgs e)
    {
        TaskManagerRequested?.Invoke();
    }
}
