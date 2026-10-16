using System;
using System.IO;
using System.Threading;
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

        options = DetachWorkingDirectory(options);
        SetupLog.Info("Setup started: " + string.Join(' ', args));
        using var owner = new Mutex(false, @"Global\WSGM.SetupOwner", out var createdNew);
        if (!createdNew)
        {
            SetupLog.Warn("Another WSGM setup is already running. Use its existing window.");
            return QuietSetup.Failed;
        }

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

    /// <summary>Releases an inherited installation directory without changing relative command-line inputs.</summary>
    /// <param name="options">Options interpreted relative to the launcher's original working directory.</param>
    /// <returns>Options with absolute input paths, usable after setup leaves the installation directory.</returns>
    internal static SetupOptions DetachWorkingDirectory(SetupOptions options)
    {
        options = options with
        {
            AnswersFile = options.AnswersFile is { } answers ? Path.GetFullPath(answers) : null,
            PayloadDirectory = options.PayloadDirectory is { } payload ? Path.GetFullPath(payload) : null
        };
        Directory.SetCurrentDirectory(Path.GetTempPath());
        return options;
    }

    /// <summary>Avalonia configuration, also used by the designer.</summary>
    /// <returns>Configured Avalonia builder; application lifetime has not started.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<SetupApp>()
            .UsePlatformDetect()
            .WithInterFont();
    }
}
