using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenFSE.Core;
using OpenFSE.Input;

namespace OpenFSE.Settings;

/// <summary>First-run / update dialog shown when OpenFSE runs portable. One obvious
/// choice: install (or update the existing install), or continue portable.</summary>
public partial class WelcomeWindow : Window
{
    private readonly GamepadService _gamepad = new();
    private GamepadNavigation? _navigation;

    public WelcomeWindow()
    {
        InitializeComponent();

        if (Installer.IsAppInstalled)
        {
            TitleText.Text = "Update OpenFSE?";
            InstallButton.Content = "Update installed app";
            BodyText.Text = "An installed copy of OpenFSE already exists. Update it with this version? " +
                            "Your settings and shell registration are kept.";
        }

        Opened += (_, _) =>
        {
            InstallButton.Focus();
            _navigation = new GamepadNavigation(_gamepad, this, back: OpenPortableSettings,
                isNintendoLayout: () => ConfigStore.Load().GlyphStyle == GlyphStyle.Nintendo);
            _gamepad.Start();
        };
        Closed += (_, _) =>
        {
            _gamepad.Stop();
            _navigation?.Dispose();
        };
    }

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        try
        {
            var installedExe = Installer.InstallApp();
            // Hand over to the installed copy so everything (shortcut, shell
            // registration, settings) runs from the stable path from here on.
            Process.Start(new ProcessStartInfo(installedExe, "--settings") { UseShellExecute = true });
            Close();
            (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            BodyText.Text = $"Installation failed: {ex.Message}";
        }
    }

    private void OnPortable(object? sender, RoutedEventArgs e) => OpenPortableSettings();

    private void OpenPortableSettings()
    {
        var settings = new SettingsWindow();
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = settings;
        }
        settings.Show();
        Close();
    }
}
