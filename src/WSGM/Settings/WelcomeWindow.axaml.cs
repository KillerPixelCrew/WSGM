using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

/// <summary>First-run / update dialog shown when WSGM runs portable. One obvious
/// choice: install (or update the existing install), or continue portable.</summary>
public partial class WelcomeWindow : Window
{
    private readonly GamepadService _gamepad = new();
    private GamepadNavigation? _navigation;

    /// <summary>Creates the portable-install welcome window.</summary>
    public WelcomeWindow()
    {
        InitializeComponent();

        if (Installer.IsAppInstalled)
        {
            TitleText.Text = "Update WSGM?";
            InstallButton.Content = "Update installed app";
            BodyText.Text = "An installed copy of WSGM already exists. Update it with this version? " +
                            "Your settings and shell registration are kept.";
        }

        Opened += (_, _) =>
        {
            InstallButton.Focus();
            // Read once: the delegate runs on every gamepad button press (and each
            // auto-repeat tick) — no per-press file read/JSON parse.
            var nintendoLayout = ConfigStore.Load().GlyphStyle == GlyphStyle.Nintendo;
            _navigation = new GamepadNavigation(_gamepad, this, back: OpenPortableSettings,
                isNintendoLayout: () => nintendoLayout);
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
            if (!AppLauncher.Open(installedExe, "--settings").Started)
            {
                // Failure already logged by AppLauncher. Stay open like the old
                // catch path did — don't shut down with nothing handed over.
                BodyText.Text = "Installed, but starting the installed copy failed — see the log.";
                return;
            }
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
