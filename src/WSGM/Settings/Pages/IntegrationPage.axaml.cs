using Avalonia.Controls;

namespace WSGM.Settings.Pages;

/// <summary>The Steam CEF integration page: the master injection switch and the
/// per-feature sub-toggles. Inherits the window's <see cref="SettingsViewModel"/>
/// DataContext and has no code-behind behavior of its own.</summary>
public partial class IntegrationPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public IntegrationPage() => InitializeComponent();
}
