using Avalonia.Controls;

namespace WSGM.Settings.Pages;

/// <summary>The System settings page: shell status hero row, app install,
/// sign-in start choices, legacy-shell restore, recovery help and diagnostics.
/// Inherits the window's <see cref="SettingsViewModel"/> DataContext.</summary>
public partial class SystemPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public SystemPage() => InitializeComponent();
}
