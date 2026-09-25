using System;
using Avalonia;
using WSGM.Setup.Engine;
using WSGM.Setup.UI;

namespace WSGM.Setup;

/// <summary>Setup's entry point: a window by default, or a quiet run for scripts and the updater.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args, out var error);
        if (options is null)
        {
            SetupLog.Error(error ?? "Invalid arguments.");
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(SetupOptions.Usage);
            return 64;
        }

        SetupLog.Info("Setup started: " + string.Join(' ', args));
        if (options.Quiet)
        {
            try
            {
                return QuietSetup.Run(options);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetupLog.Error("Quiet setup failed", ex);
                return QuietSetup.Failed;
            }
        }

        SetupApp.Options = options;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Avalonia configuration, also used by the designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<SetupApp>()
            .UsePlatformDetect()
            .WithInterFont();
    }
}
