using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenFSE.Core;

namespace OpenFSE.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var installed = ShellRegistration.IsInstalledForThisExe();
        StatusText.Text = installed
            ? "OpenFSE IS your Windows shell for this account. Sign out and back in for changes to take effect."
            : "OpenFSE is NOT your Windows shell.";
        InstallButton.IsEnabled = !installed;
        UninstallButton.IsEnabled = installed;
    }

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        var config = ConfigStore.Load();
        ShellRegistration.Install(config);
        UpdateStatus();
    }

    private void OnUninstall(object? sender, RoutedEventArgs e)
    {
        ShellRegistration.Uninstall();
        UpdateStatus();
    }
}
