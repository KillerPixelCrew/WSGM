using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WSGM.Overlay;

/// <summary>Tools > Storage: safe removal and formatting an SD card as a Steam library.</summary>
public sealed partial class StoragePage : StackPanel
{
    private StorageFormatView? _format;

    /// <summary>Builds the rows; the window supplies the format flow with <see cref="Configure" />.</summary>
    public StoragePage()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user asks to eject a drive.</summary>
    internal event Action? EjectRequested;

    /// <summary>Raised once the format flow lists its targets, which the window then shows.</summary>
    internal event Action? FormatPageRequested;

    /// <summary>Supplies the format flow the Format row starts.</summary>
    /// <param name="format">The window's format flow.</param>
    internal void Configure(StorageFormatView format)
    {
        _format = format;
    }

    private void OnEject(object? sender, RoutedEventArgs e)
    {
        EjectRequested?.Invoke();
    }

    private void OnFormatSdCard(object? sender, RoutedEventArgs e)
    {
        if (_format is not { Available: true } format)
        {
            return;
        }

        format.ShowTargets();
        FormatPageRequested?.Invoke();
        format.RefreshTargets();
    }
}
