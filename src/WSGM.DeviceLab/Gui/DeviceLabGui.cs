using System;
using Avalonia;
using Avalonia.Controls;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Gui;

/// <summary>Composes the developer tabs or the wizard with one set of path boundaries.</summary>
internal static class DeviceLabGui
{
    /// <summary>Runs the developer tabs.</summary>
    /// <param name="args">Avalonia arguments.</param>
    /// <param name="repositoryRoot">The root <see cref="Program" /> found, or null outside a checkout.</param>
    /// <returns>Exit code.</returns>
    internal static int Run(string[] args, string? repositoryRoot)
    {
        DeviceLabApplication application = new(repositoryRoot, DeviceLabExecutable.CurrentPath);
        return Start(() => new MainWindow(application, application.Boundaries), args);
    }

    /// <summary>Runs the tester wizard.</summary>
    /// <param name="options">How the wizard was started.</param>
    /// <param name="repositoryRoot">The root <see cref="Program" /> found, or null outside a checkout.</param>
    /// <returns>Exit code.</returns>
    internal static int RunWizard(WizardOptions options, string? repositoryRoot)
    {
        var boundaries = DeviceLabPathBoundaries.ForCurrentUser(repositoryRoot);
        return Start(() => new WizardWindow(options, boundaries), []);
    }

    private static int Start(Func<Window> mainWindow, string[] args)
    {
        return AppBuilder.Configure(() => new App(mainWindow))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
