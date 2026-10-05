using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace WSGM.DeviceLab.Gui;

/// <summary>The Avalonia application; it opens whichever window the entry point composed.</summary>
/// <param name="mainWindow">Creates the main window once the framework is ready.</param>
internal sealed class App(Func<Window> mainWindow) : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
