using Avalonia.Controls;

namespace WSGM.Settings.Pages;

/// <summary>
///     The System settings page: shell status hero row, sign-in start, Steam start,
///     device-manager takeover and start mode, updates, recovery help, power and diagnostics.
///     Inherits the window's <see cref="SettingsViewModel" /> DataContext.
/// </summary>
public partial class SystemPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public SystemPage()
    {
        InitializeComponent();
    }
}
