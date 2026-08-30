using Avalonia.Controls;

namespace WSGM.Settings.Pages;

/// <summary>Settings the installed device plugin declares, rendered from its manifest.</summary>
/// <remarks>
/// The page owns no controls of its own. Its content comes from the one projection in
/// <c>PluginSettingsCoordinator.Project</c>, shared with the overlay so both surfaces order and
/// place a plugin's settings identically, and it changes with whichever plugin is installed — which
/// is why the sections and rows are bound rather than written here.
/// </remarks>
public partial class PluginSettingsPage : UserControl
{
    /// <summary>Loads the compiled page XAML.</summary>
    public PluginSettingsPage() => InitializeComponent();
}
