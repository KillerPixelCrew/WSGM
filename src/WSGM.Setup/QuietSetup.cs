using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WSGM.Setup.Engine;

namespace WSGM.Setup;

/// <summary>
///     Setup without a window, for development deploys, the updater and managed installs. It takes the
///     same steps as the pages; only the choices come from defaults, the current install or arguments.
/// </summary>
internal static class QuietSetup
{
    /// <summary>Exit codes a caller can act on.</summary>
    internal const int Success = 0;

    internal const int Failed = 1;
    internal const int NewerInstalled = 2;
    internal const int ControllerStillHidden = 3;
    internal const int SteamMissing = 4;
    internal const int NoPayload = 5;

    /// <summary>
    ///     WSGM's autostart is off and Windows must restart before the USB/IP driver can be installed; setup
    ///     runs again on its own afterwards. A quiet run does not restart the machine itself.
    /// </summary>
    internal const int RestartToFinishDrivers = 6;

    /// <summary>Runs the requested mode and returns the exit code.</summary>
    public static int Run(SetupOptions options)
    {
        using var engine = SetupEngine.Detect(options.PayloadDirectory);
        if (options.Mode is SetupMode.Uninstall)
        {
            var uninstall = engine.PlanUninstall(new UninstallChoices(!options.RemoveData, !options.KeepComponents,
                !options.KeepComponents));
            var ok = engine.Run(uninstall, () => { });
            return engine.StillHiddenDevices.Count > 0 ? ControllerStillHidden : ok ? Success : Failed;
        }

        if (engine.Kind is SetupKind.NewerInstalled)
        {
            SetupLog.Warn($"WSGM {engine.InstalledVersion} is newer than this setup; nothing was changed.");
            return NewerInstalled;
        }

        if (!engine.SteamInstalled)
        {
            SetupLog.Warn("Steam is not installed; WSGM needs it.");
            return SteamMissing;
        }

        if (engine.Payload is null || engine.Offers is null)
        {
            SetupLog.Warn("This setup carries no payload; pass /payload=<dir> for a development build.");
            return NoPayload;
        }

        if (options.Mode is SetupMode.FinishDrivers)
        {
            return Finish(engine, engine.Run(engine.PlanFinishDrivers(), () => { }), false);
        }

        var exported = engine.PrepareAnswers();
        var answers = options.AnswersFile is { } file
            ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidDataException("Empty answers.")
            : exported;
        var fresh = exported["freshInstall"]?.GetValue<bool>() == true;
        if (fresh && options.AnswersFile is null)
        {
            // A silent fresh install never consents to taking over how Steam starts.
            answers["steamAutostartTakeover"] = false;
            answers["otherManagersTakeover"] = false;
        }

        var device = DevicePlugin(options, engine);
        var common = fresh
            ? []
            : engine.Offers.Common.Where(offer => offer.Installed).Select(offer => offer.Plugin.Id).ToArray();
        answers["deviceIntegration"] = device is not null;
        var plan = engine.PlanInstall(new InstallChoices(device, common, answers));
        return Finish(engine, engine.Run(plan, () => { }), !fresh);
    }

    /// <summary>Maps a finished run to its exit code and starts WSGM when that is wanted and safe.</summary>
    private static int Finish(SetupEngine engine, bool succeeded, bool startWsgm)
    {
        if (engine.DriverUpdatePending)
        {
            SetupLog.Info("WSGM's autostart is off; restart Windows to let setup install the USB/IP driver.");
            return RestartToFinishDrivers;
        }

        if (succeeded && startWsgm)
        {
            engine.StartWsgm();
        }

        return succeeded ? Success : Failed;
    }

    private static string? DevicePlugin(SetupOptions options, SetupEngine engine)
    {
        if (string.Equals(options.Plugin, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (options.Plugin is { } id)
        {
            return engine.Payload!.Bundle.Plugins.Any(plugin => plugin.Id == id && plugin.IsDevice)
                ? id
                : throw new ArgumentException($"This setup does not bundle a device plugin named {id}.");
        }

        // An update or repair keeps the device plugin the user has; a fresh install follows detection
        // and installs nothing when the choice is ambiguous.
        return engine.Offers!.DeviceCandidates.FirstOrDefault(offer => offer.Installed)?.Plugin.Id
               ?? engine.Offers.RecommendedDevice?.Plugin.Id;
    }
}
