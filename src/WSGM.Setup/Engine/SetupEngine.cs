using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WSGM.Install;
using WSGM.Shared;

namespace WSGM.Setup.Engine;

/// <summary>What setup found when it started.</summary>
internal enum SetupKind
{
    /// <summary>Nothing installed.</summary>
    Install,

    /// <summary>An older WSGM 2 is installed.</summary>
    Update,

    /// <summary>This release is installed.</summary>
    Maintain,

    /// <summary>A newer release is installed; this setup refuses to downgrade it.</summary>
    NewerInstalled
}

/// <summary>How one step ended.</summary>
internal enum StepState
{
    /// <summary>Not yet run.</summary>
    Waiting,

    /// <summary>The step callback is executing.</summary>
    Running,

    /// <summary>The callback succeeded without selecting Skipped.</summary>
    Done,

    /// <summary>The callback refused or threw; Fatal determines whether subsequent steps run.</summary>
    Failed,

    /// <summary>No action was needed, as selected by the callback.</summary>
    Skipped
}

/// <summary>One line of the progress page.</summary>
/// <param name="label">Label while pending or running.</param>
/// <param name="doneLabel">Label after success or a skip.</param>
/// <param name="fatal">Whether failure stops the plan and enters rollback.</param>
/// <param name="run">Synchronous step callback; updates this step's detail and returns success.</param>
internal sealed class SetupStep(string label, string doneLabel, bool fatal, Func<SetupStep, bool> run)
{
    /// <summary>Pending/running presentation label.</summary>
    public string Label { get; } = label;

    /// <summary>Success/skip presentation label, which a step may refine.</summary>
    public string DoneLabel { get; set; } = doneLabel;

    /// <summary>Whether a failed step stops subsequent work.</summary>
    public bool Fatal { get; } = fatal;

    /// <summary>Current execution status, published by SetupEngine.Run.</summary>
    public StepState State { get; set; } = StepState.Waiting;

    /// <summary>What went wrong or what the user should know, or empty.</summary>
    public string Note { get; set; } = "";

    /// <summary>A hint the progress page shows while the step runs.</summary>
    public string Hint { get; init; } = "";

    internal bool Run()
    {
        return run(this);
    }
}

/// <summary>What the user chose for an install, update or repair.</summary>
/// <param name="DevicePluginId">The device plugin to install, or null for none.</param>
/// <param name="CommonPluginIds">Common plugins to install.</param>
/// <param name="Answers">The setup answers to apply, as exported by WSGM and edited by the pages.</param>
internal sealed record InstallChoices(
    string? DevicePluginId,
    IReadOnlyList<string> CommonPluginIds,
    JsonObject Answers);

/// <summary>What the user chose for uninstall.</summary>
/// <param name="KeepData">Whether per-user data should remain; unresolved recovery data is retained regardless.</param>
/// <param name="RemoveUsbip">Whether to remove a USB/IP installation recorded as WSGM-owned.</param>
/// <param name="RemoveHidHide">Whether to remove a HidHide installation recorded as WSGM-owned.</param>
internal sealed record UninstallChoices(bool KeepData, bool RemoveUsbip, bool RemoveHidHide);

/// <summary>Owns setup plans, runtime shutdown, file rollback and component/recovery obligations.</summary>
internal sealed class SetupEngine : IDisposable
{
    private readonly Func<string, bool> _delete;
    private readonly string _machineData;
    private readonly string _root;
    private readonly IRuntimeShutdown _runtime;
    private readonly Action _unregister;
    private readonly string _userData;
    private bool _controllerRestored;
    private SetupFileTransaction? _files;
    private Mutex? _owner;
    private bool _runtimeCaptured;
    private string? _runtimeExe;
    private bool _runtimeWasRunning;
    private bool _runtimeWasShell;
    private ServiceState _service;
    private bool _shutdownApplied;
    private bool _swapped;
    private string? _uninstallExe;
    private bool _uninstallPrepared;

    /// <summary>An engine that has read nothing yet; <see cref="Detect" /> is the real entry point.</summary>
    /// <param name="payload">The payload, or null for a setup that carries none.</param>
    /// <param name="runtime">The operations that stop WSGM, its service and Steam.</param>
    /// <param name="root">The installation root.</param>
    /// <param name="machineData">The machine record directory.</param>
    /// <param name="userData">The user's WSGM data directory.</param>
    /// <param name="delete">Deletes files now or schedules them for reboot; null uses Windows.</param>
    /// <param name="unregister">Removes setup's Windows registration and shortcuts; null uses Windows.</param>
    internal SetupEngine(SetupPayload? payload, IRuntimeShutdown runtime, string root, string machineData,
        string userData, Func<string, bool>? delete = null, Action? unregister = null)
    {
        Payload = payload;
        _runtime = runtime;
        _delete = delete ?? WindowsSetup.DeleteOrScheduleAtReboot;
        _unregister = unregister ?? WindowsSetup.RemoveSetupRegistration;
        _root = Path.GetFullPath(root);
        _machineData = Path.GetFullPath(machineData);
        _userData = Path.GetFullPath(userData);
    }

    private string App => Path.Combine(_root, "App");
    private string AppExe => Path.Combine(App, "WSGM.exe");
    private string AppStaging => App + ".staging";
    private string AppPrevious => App + ".previous";
    private string Plugins => Path.Combine(_root, "Plugins");
    private string SetupDirectory => Path.Combine(_root, "Setup");
    private string SetupExe => Path.Combine(SetupDirectory, "WSGM.Setup.exe");
    private string SetupPackages => Path.Combine(SetupDirectory, "Packages");
    private string InstalledBundle => Path.Combine(_machineData, "bundle.json");
    private string ComponentsFile => Path.Combine(_machineData, "components.json");
    private string PendingPluginRemovals => Path.Combine(_machineData, "plugin-removals.json");

    /// <summary>Owned payload, or null for a build containing no installation files.</summary>
    public SetupPayload? Payload { get; }

    /// <summary>Install/update/maintenance decision from the detected version.</summary>
    public SetupKind Kind { get; private set; }

    /// <summary>Registered installed version, or null when absent.</summary>
    public Version? InstalledVersion { get; private set; }

    /// <summary>Four-part payload version, including the build revision used to distinguish updates.</summary>
    public Version ThisVersion { get; } =
        typeof(SetupEngine).Assembly.GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0))
            : new Version(0, 0);

    /// <summary>The WSGM 1.0 install to remove first, or null.</summary>
    public (string Command, string Version)? Legacy { get; private set; }

    /// <summary>Whether detection found the Steam prerequisite.</summary>
    public bool SteamInstalled { get; private set; }

    /// <summary>Hardware-matched bundled offers, or null without a payload.</summary>
    public PluginOffers? Offers { get; private set; }

    /// <summary>Ids of bundled plugins that have a package file installed now.</summary>
    public IReadOnlyList<string> InstalledPluginIds { get; private set; } = [];

    /// <summary>The answers WSGM exported, or null until <see cref="PrepareAnswers" /> ran.</summary>
    public JsonObject? ExportedAnswers { get; private set; }

    /// <summary>Persisted ownership of components installed by WSGM, distinct from current presence.</summary>
    public InstalledComponents Components { get; set; } = new();

    /// <summary>Whether the last install asked for a restart.</summary>
    public bool RestartRequired { get; private set; }

    /// <summary>
    ///     Whether this run turned WSGM's autostart off and is waiting for the restart that lets it
    ///     replace the USB/IP driver. Nothing may start WSGM while this is set.
    /// </summary>
    public bool DriverUpdatePending { get; private set; }

    /// <summary>Whether setup scheduled itself to open again after that restart.</summary>
    public bool DriverUpdateResumes { get; private set; }

    /// <summary>Whether the uninstall could not confirm the controller is visible again.</summary>
    public IReadOnlyList<string> StillHiddenDevices { get; private set; } = [];

    /// <summary>
    ///     Whether rollback could not stop the service, restore the previous program files or start the
    ///     sign-in service that was running before.
    /// </summary>
    public bool RollbackIncomplete { get; private set; }

    // The informational version carries the commit, so the log says exactly which build ran.
    private static string Build =>
        typeof(SetupEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown build";

    private string LogonServiceExe => Path.Combine(App, "WSGM.LogonService.exe");

    /// <summary>Releases the device reservation and payload; callers must finish or roll back the plan first.</summary>
    public void Dispose()
    {
        _owner?.Dispose();
        Payload?.Dispose();
    }

    /// <summary>A version as setup shows it: the release, and the build when it has one.</summary>
    /// <param name="version">The version.</param>
    /// <returns>For example <c>2.0.0 (build 1350)</c>.</returns>
    public static string Display(Version? version)
    {
        return version is null ? string.Empty
            : version.Revision > 0 ? $"{version.ToString(3)} (build {version.Revision})"
            : version.ToString(3);
    }

    /// <summary>Reads the machine: what is installed, the payload, and the offers for this hardware.</summary>
    /// <param name="payloadDirectory">Development payload override, or null for the embedded release payload.</param>
    /// <returns>An owned engine after recovering any unfinished file transaction.</returns>
    /// <exception cref="WrongSetupAccountException">The elevated account differs from the interactive account.</exception>
    /// <remarks>Detection can stop the runtime and recover an interrupted install; it is not a read-only inventory.</remarks>
    public static SetupEngine Detect(string? payloadDirectory)
    {
        SetupUserIdentity.RequireCurrentSessionUser();
        SetupEngine engine = new(SetupPayload.Open(payloadDirectory), new WindowsRuntimeShutdown(),
            InstallLayout.Root, InstallLayout.MachineData,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM"));
        // Choices must see a coherent installed package set, never the interrupted replacement.
        if (InstallLayout.HasPendingSetup)
        {
            var recovery = new SetupStep("Recovering interrupted setup", "Previous installation restored", true,
                _ => true);
            try
            {
                if (!engine.StopRuntime(recovery, false))
                {
                    throw new InvalidOperationException(recovery.Note);
                }

                // Recovery is complete. Restore the old runtime before the interactive choice pages.
                engine.RollBack();
            }
            catch
            {
                engine.Dispose();
                throw;
            }
        }
        else if (File.Exists(InstallLayout.SetupTransaction))
        {
            engine.CreateFileTransaction().Recover();
        }

        engine.SteamInstalled = WindowsSetup.SteamInstalled();
        engine.Legacy = Registration.LegacyInstall();
        engine.InstalledVersion = Registration.InstalledVersion();
        engine.Components = InstalledComponents.Read(engine.ComponentsFile);
        engine.Kind = engine.InstalledVersion switch
        {
            null => SetupKind.Install,
            { } installed when installed < engine.ThisVersion => SetupKind.Update,
            { } installed when installed > engine.ThisVersion => SetupKind.NewerInstalled,
            _ => SetupKind.Maintain
        };
        if (engine.Payload is { } payload)
        {
            var adapters = DisplayAdapterInventory.Collect();
            engine.InstalledPluginIds = engine.InstalledIds(payload.Bundle);
            engine.Offers = PluginOffers.Compute(payload.Bundle, DeviceMachineIdentity.Collect(), adapters,
                engine.InstalledPluginIds);
            SetupLog.Info("Display adapters: "
                          + (adapters.Count == 0
                              ? "none"
                              : string.Join(", ",
                                  adapters.Select(adapter => $"{adapter.PciVendorId}:{adapter.PciDeviceId}")))
                          + "; graphics plugins for them: "
                          + (engine.Offers.Gpu.Count == 0
                              ? "none"
                              : string.Join(", ", engine.Offers.Gpu.Select(offer => offer.Plugin.Id)))
                          + ".");
        }

        SetupLog.Info(
            $"Setup {engine.ThisVersion} ({Build}, {Environment.ProcessPath}): kind={engine.Kind}, installed={engine.InstalledVersion}, "
            + $"legacy={engine.Legacy?.Version ?? "none"}, steam={engine.SteamInstalled}, "
            + $"payload={engine.Payload?.Source ?? "none"}.");
        return engine;
    }

    /// <summary>
    ///     Unpacks the new application beside the installed one and asks it for the current answers, so
    ///     the profile page starts from the user's values, or from the defaults on a fresh install.
    /// </summary>
    /// <returns>The exported mutable answers object, also retained as ExportedAnswers.</returns>
    public JsonObject PrepareAnswers()
    {
        var payload = Payload ?? throw new InvalidOperationException("This setup carries no payload.");
        if (Directory.Exists(AppStaging))
        {
            Directory.Delete(AppStaging, true);
        }

        payload.Extract("App", AppStaging);
        var file = Path.Combine(Path.GetTempPath(), $"wsgm-answers-{Guid.NewGuid():N}.json");
        try
        {
            var code = _runtime.Run(Path.Combine(AppStaging, "WSGM.exe"), $"--export-setup-answers=\"{file}\"");
            ExportedAnswers = code == 0 && File.Exists(file)
                ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject
                  ?? throw new InvalidDataException("WSGM exported no answers.")
                : throw new InvalidDataException($"WSGM could not export its settings (exit {code}).");
            return ExportedAnswers;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    ///     The common and graphics plugins an update or repair keeps: every bundled one installed now,
    ///     including a graphics package whose adapter is currently absent, such as an unplugged external GPU.
    /// </summary>
    /// <returns>Installed common/graphics ids still represented by this payload.</returns>
    public IReadOnlyList<string> InstalledCommonPluginIds()
    {
        return Payload?.Bundle.Plugins
            .Where(plugin => !plugin.IsDevice && InstalledPluginIds.Contains(plugin.Id))
            .Select(plugin => plugin.Id)
            .ToArray() ?? [];
    }

    /// <summary>
    ///     Graphics plugins for a present adapter that are not installed yet, including update and repair offers.
    /// </summary>
    /// <returns>Hardware-matched graphics offers absent from the installed package set.</returns>
    public IReadOnlyList<PluginOffer> NewGpuOffers()
    {
        return Offers?.Gpu.Where(offer => !offer.Installed).ToArray() ?? [];
    }

    /// <summary>Preserves update choices and adds the selected new graphics packages once.</summary>
    /// <param name="answers">Mutable answers; device integration is disabled here if no installed device matches.</param>
    /// <param name="addedGpuIds">New graphics packages accepted by the caller.</param>
    /// <returns>Choices retaining installed bundled common packages and the surviving matching device.</returns>
    internal InstallChoices KeptChoices(JsonObject answers, IEnumerable<string> addedGpuIds)
    {
        var device = Offers?.DeviceCandidates.FirstOrDefault(offer => offer.Installed)?.Plugin.Id;
        if (device is null)
        {
            answers["deviceIntegration"] = false;
        }

        return new InstallChoices(device,
            InstalledCommonPluginIds().Concat(addedGpuIds).Distinct(StringComparer.Ordinal).ToArray(), answers);
    }

    /// <summary>The system components the chosen plugins need.</summary>
    /// <param name="choices">Selected bundled device and common plugin ids.</param>
    /// <returns>Distinct required components, or an empty list without a payload.</returns>
    public IReadOnlyList<SetupComponent> RequiredComponents(InstallChoices choices)
    {
        return Payload?.Bundle.Plugins
            .Where(plugin => plugin.Id == choices.DevicePluginId || choices.CommonPluginIds.Contains(plugin.Id))
            .SelectMany(plugin => SetupComponents.Required(plugin.Capabilities))
            .Distinct()
            .ToArray() ?? [];
    }

    /// <summary>Whether the controller stack must be installed because it is missing.</summary>
    /// <param name="choices">Chosen plugins whose roles determine the stack requirement.</param>
    /// <returns>Whether the stack is required and USB/IP or HidHide is absent; installed versions are not checked here.</returns>
    public bool NeedsDrivers(InstallChoices choices)
    {
        return RequiredComponents(choices).Contains(SetupComponent.ControllerStack)
               && (!InstalledComponents.UsbipPresent() || !InstalledComponents.HidHidePresent());
    }

    /// <summary>The steps of an install, update or repair.</summary>
    /// <param name="choices">Accepted package selection and setup answers.</param>
    /// <returns>An ordered plan to execute once through Run.</returns>
    public IReadOnlyList<SetupStep> PlanInstall(InstallChoices choices)
    {
        var payload = Payload ?? throw new InvalidOperationException("This setup carries no payload.");
        var controller = RequiredComponents(choices).Contains(SetupComponent.ControllerStack);
        List<SetupStep> steps = [];
        if (Legacy is { } legacy)
        {
            steps.Add(new SetupStep("Removing WSGM " + legacy.Version, "WSGM " + legacy.Version + " removed", true,
                step => RemoveLegacy(step, legacy.Command)));
        }

        steps.Add(new SetupStep("Closing WSGM and Steam", "WSGM and Steam closed", true,
            step => StopRuntime(step, false)));
        steps.Add(new SetupStep("Copying WSGM", $"WSGM {Display(ThisVersion)} installed", true,
            step => InstallApplication(step, controller)));
        steps.Add(new SetupStep("Keeping this setup for repair and uninstall", "Setup stored for repair", true,
            _ => StoreSetup(payload)));
        foreach (var id in new[] { choices.DevicePluginId }.Concat(choices.CommonPluginIds).OfType<string>())
        {
            var plugin = payload.Bundle.Plugins.First(candidate => candidate.Id == id);
            steps.Add(new SetupStep("Installing " + plugin.Name, $"{plugin.Name} {plugin.Version} installed", true,
                _ => InstallPlugin(payload, plugin)));
        }

        steps.Add(new SetupStep("Applying your profile", "Profile applied", true,
            step => ApplyAnswers(step, choices.Answers)));
        steps.Add(RegisterServiceStep("Registering the sign-in service", "Sign-in service registered", false));
        if (controller)
        {
            steps.Add(UsbipStep(false));
            steps.Add(new SetupStep("Installing HidHide", "HidHide installed", false, InstallHidHide));
        }

        // RTSS is the one component setup downloads rather than carries; a failure leaves WSGM working without it.
        if (choices.Answers["features"]?["rtss"]?.GetValue<bool>() == true)
        {
            steps.Add(new SetupStep("Installing RivaTuner Statistics Server",
                $"RivaTuner Statistics Server {RtssInstaller.Version} installed", false, RtssInstaller.Install)
            {
                Hint = "Setup downloads RTSS from Guru3D's mirror now."
            });
        }

        steps.Add(new SetupStep("Installing media preview runtime", "WebView2 media runtime installed", false,
            step => WebViewRuntimeInstaller.Install(payload, step)));

        steps.Add(new SetupStep("Adding Start menu entries", "Start menu entries added", false, _ => Register()));
        return steps;
    }

    /// <summary>The steps of an uninstall.</summary>
    /// <param name="choices">Which user data and optional components to retain or remove.</param>
    /// <returns>Ordered deferred steps; creating the plan does not execute them.</returns>
    public IReadOnlyList<SetupStep> PlanUninstall(UninstallChoices choices)
    {
        List<SetupStep> steps =
        [
            new("Closing WSGM and Steam", "WSGM and Steam closed", true, step => StopRuntime(step, true)),
            new("Removing the Steam Input shim", "Steam Input shim removed", false,
                _ => RunUninstallCommand("--remove-steam-input-shim")),
            new("Restoring Steam's guide chord template", "Steam's guide chord template restored", false,
                _ => RunUninstallCommand("--restore-steam-chord-template")),
            new("Restoring Steam's boot movie and themes folder", "Steam's boot movie and themes folder restored",
                false, RestoreSteamContent),
            new("Removing the sign-in service", "Sign-in service removed", false,
                _ => RunUninstallCommand("--uninstall", "WSGM.LogonService.exe")),
            new("Restoring the shell registration", "Shell registration restored", false,
                _ => RunUninstallCommand("--unregister-shell")),
            new("Showing your controller to games again and restoring Windows settings",
                "Controller shown to games again, Windows settings restored", false, RestoreController)
        ];
        if (choices.RemoveUsbip && Components.Usbip)
        {
            steps.Add(new SetupStep("Removing the USB/IP driver", "USB/IP driver removed", false,
                step => RemoveComponent(step, "USBip", Registration.FindUsbipUninstallCommand))
            {
                Hint = "Your controls drop out for a few seconds now."
            });
        }

        if (choices.RemoveHidHide && Components.HidHide)
        {
            steps.Add(new SetupStep("Removing HidHide", "HidHide removed", false,
                step => RemoveComponent(step, "HidHide", () => Registration.FindUninstallCommand("HidHide"))));
        }

        steps.Add(new SetupStep("Deleting program files", "Program files deleted", false, DeleteProgramFiles));
        if (!choices.KeepData)
        {
            steps.Add(new SetupStep("Deleting settings and data", "Settings and data deleted", false,
                step => DeleteUserData(step, _userData, _delete)));
        }

        return steps;
    }

    /// <summary>Runs the steps in order. A fatal failure rolls an install back and stops.</summary>
    /// <param name="steps">The plan.</param>
    /// <param name="changed">Called after every state change, from the running thread.</param>
    /// <returns>Whether every fatal step succeeded.</returns>
    public bool Run(IReadOnlyList<SetupStep> steps, Action changed)
    {
        foreach (var step in steps)
        {
            step.State = StepState.Running;
            changed();
            bool ok;
            try
            {
                ok = step.Run();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetupLog.Error(step.Label + " failed", ex);
                step.Note = step.Note.Length > 0 ? step.Note : ex.Message;
                ok = false;
            }

            step.State = ok ? step.State is StepState.Skipped ? StepState.Skipped : StepState.Done : StepState.Failed;
            SetupLog.Info($"{step.Label}: {step.State}{(step.Note.Length > 0 ? " — " + step.Note : "")}");
            changed();
            if (!ok && step.Fatal)
            {
                RollBack();
                return false;
            }
        }

        try
        {
            _files?.Commit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetupLog.Error("Committing the installed files failed", ex);
            RollBack();
            return false;
        }

        FinishInstall();
        return true;
    }

    /// <summary>Restarts Windows now, for the boot the driver update needs.</summary>
    public static void RestartWindows()
    {
        SetupLog.Info("Restarting Windows to install the USB/IP driver with nothing attached to it.");
        WindowsSetup.Run(WindowsSetup.SystemTool("shutdown.exe"), "/r /t 0");
    }

    /// <summary>
    ///     The steps of the run after that restart: the USB/IP driver, then WSGM's autostart back on.
    ///     Nothing else is touched; the install itself was finished before the restart.
    /// </summary>
    /// <returns>Driver installation and autostart-restoration steps; throws if this setup has no payload.</returns>
    public IReadOnlyList<SetupStep> PlanFinishDrivers()
    {
        _ = Payload ?? throw new InvalidOperationException("This setup carries no payload.");
        return
        [
            UsbipStep(true),
            RegisterServiceStep("Turning WSGM's autostart back on", "WSGM autostart on", false)
        ];
    }

    /// <summary>The service's own installer: creates or reconfigures it as auto-start and starts it.</summary>
    private SetupStep RegisterServiceStep(string label, string doneLabel, bool fatal)
    {
        return new SetupStep(label, doneLabel, fatal, step => Fail(step,
            _runtime.Run(LogonServiceExe, "--install") == 0,
            "The sign-in service could not be registered; see setup.log."));
    }

    /// <param name="install">
    ///     Replace the driver when it is outdated. Only the run after the driver-update restart may:
    ///     everywhere else the step arranges that restart instead.
    /// </param>
    private SetupStep UsbipStep(bool install)
    {
        return new SetupStep("Installing the USB/IP driver", "USB/IP driver installed", false,
            step => InstallUsbip(step, install))
        {
            Hint = "Your controls drop out for a few seconds now."
        };
    }

    /// <summary>Starts WSGM the way it was running before, or the session on a fresh install.</summary>
    /// <remarks>
    ///     Does nothing while a driver update is pending: WSGM attaches its virtual pad, which is
    ///     exactly what the restart this run asked for is meant to avoid.
    /// </remarks>
    public void StartWsgm()
    {
        var app = AppExe;
        if (DriverUpdatePending || !File.Exists(app))
        {
            return;
        }

        _runtime.Start(app, _runtimeWasShell ? "--shell" : _runtimeWasRunning ? "" : "--shell --activate");
    }

    /// <summary>Removes WSGM 1.0 through its own uninstaller.</summary>
    /// <param name="step">The step to label.</param>
    /// <param name="command">The 1.0 uninstall string.</param>
    /// <returns>Whether 1.0 is gone.</returns>
    internal bool RemoveLegacy(SetupStep step, string command)
    {
        // The old uninstaller signals the uninstall event, on which WSGM deliberately leaves Steam running.
        // Stop WSGM through the update event first, as the old installer's update did, so WSGM closes Steam
        // gracefully and the mode it ran in is recorded before it is gone.
        StopAndCapture(false);
        return Fail(step,
            _runtime.RunInnoUninstaller(command, () => Registration.LegacyInstall() is not null),
            "The WSGM 1.0 uninstaller did not finish. Remove WSGM 1.0 from Windows Settings, then run setup again.");
    }

    /// <summary>
    ///     Stops the sign-in service, then WSGM, then Steam, refuses while Steam or a launch wrapper stays, and
    ///     reserves the hardware owner last. Nothing is changed before the service is known to be stopped.
    /// </summary>
    /// <param name="step">The step to label.</param>
    /// <param name="forUninstall">Whether WSGM is asked to exit for uninstall rather than for an update.</param>
    /// <returns>Whether setup may change the installation now.</returns>
    internal bool StopRuntime(SetupStep step, bool forUninstall)
    {
        if (_runtime.InspectService() is not { } service)
        {
            step.Note = "The WSGM sign-in service state could not be verified, so nothing was stopped.";
            return false;
        }

        _service = service;
        if (!_runtime.StopService())
        {
            step.Note = "The WSGM sign-in service did not stop. Nothing was changed.";
            return false;
        }

        StopAndCapture(forUninstall);
        _shutdownApplied = true;

        // Every mode closes Steam. An install, update or repair lets WSGM start it with its own settings, and
        // an uninstall can only remove the Steam Input helper from Steam's folder once Steam no longer has it
        // loaded. WSGM's pre-stop covers Steam only for an update and only when WSGM was running (not on a
        // retry, after the old uninstaller, or for an uninstall), so setup sends the same graceful request
        // and waits longer. Steam is never terminated. With an installed WSGM, a Steam that stays refuses the
        // change; a fresh install has nothing loaded in Steam and continues.
        var existing = File.Exists(AppExe);
        if (!_runtime.CloseSteam(TimeSpan.FromSeconds(60)) && !existing)
        {
            SetupLog.Warn("Steam stayed open; a fresh install continues, and WSGM starts Steam its own way next time.");
        }

        var blockers = _runtime.Blockers(existing);
        if (blockers.Count > 0)
        {
            step.Note = blockers.Count == 1
                ? $"{blockers[0]} is still running. Close it normally, then run setup again. No process was ended."
                : $"{string.Join(" and ", blockers)} are still running. Close them normally, then run setup again. "
                  + "No process was ended.";
            return false;
        }

        if (forUninstall && File.Exists(Path.Combine(App, "WSGM.PackagedLaunch.exe"))
                         && _runtime.Run(Path.Combine(App, "WSGM.PackagedLaunch.exe"), "--recover") != 0)
        {
            step.Note = "An imported Xbox or Store game is still exempt from Windows suspending it, and WSGM could "
                        + "not put it back. packaged-launch.log in %LOCALAPPDATA%\\WSGM names the game.";
            return false;
        }

        _owner = _runtime.ReserveDeviceOwner(TimeSpan.FromSeconds(30));
        if (_owner is null)
        {
            step.Note = "A WSGM or Device Lab hardware owner is still active. Close it and run setup again.";
            return false;
        }

        CreateFileTransaction().Recover();
        return true;
    }

    /// <summary>
    ///     Stops WSGM and records, once, how it ran and from where. A later stop sees the temporarily stopped
    ///     state and must not overwrite it, or a rollback would restart nothing or the wrong mode.
    /// </summary>
    private void StopAndCapture(bool forUninstall)
    {
        var wasShell = _runtime.ShellRunning();
        var exe = _runtime.RunningWsgmPath();
        var handoff = StopWsgm(forUninstall);
        if (_runtimeCaptured)
        {
            return;
        }

        _runtimeWasShell = wasShell;
        _runtimeWasRunning = handoff is not ShutdownHandoff.NotRunning || wasShell;
        _runtimeExe = exe;
        _runtimeCaptured = true;
    }

    /// <summary>
    ///     Asks WSGM to exit through the update or uninstall event, then force-stops what is left of it in this
    ///     session. The shell anchor goes last and only once it settled its recovery, since it may be the only
    ///     process able to restore Explorer.
    /// </summary>
    private ShutdownHandoff StopWsgm(bool forUninstall)
    {
        var handoff = forUninstall
            ? _runtime.RequestExit(WindowsSetup.ExitForUninstall, WindowsSetup.UninstallGraceIterations)
            : _runtime.RequestExit(WindowsSetup.ExitForUpdate, WindowsSetup.UpdateGraceIterations);
        _runtime.ForceStopCurrentSession("WSGM.exe");
        if (_runtime.ShellAnchorRecoverySettled())
        {
            _runtime.ForceStopCurrentSession("WSGM.ShellAnchor.exe");
        }

        return handoff;
    }

    private SetupFileTransaction CreateFileTransaction()
    {
        return new SetupFileTransaction(_root, _machineData,
            _runtime.InstalledVersion, _runtime.RestoreVersion,
            () => SetupExecutable.Path, path => SetupExecutable.Path = path);
    }

    private bool InstallApplication(SetupStep step, bool controller)
    {
        _files = CreateFileTransaction();
        _files.Begin();
        var payload = Payload!;
        if (!Directory.Exists(AppStaging))
        {
            payload.Extract("App", AppStaging);
        }

        if (controller)
        {
            // The virtual controller library lives beside WSGM; the driver installers stay in the payload.
            var controllerStage = Path.Combine(Path.GetTempPath(), $"wsgm-controller-{Guid.NewGuid():N}");
            payload.Extract("Controller", controllerStage);
            foreach (var file in Directory.EnumerateFiles(controllerStage)
                         .Where(file =>
                             Path.GetFileName(file).StartsWith("libviiper", StringComparison.OrdinalIgnoreCase)
                             || Path.GetFileName(file).StartsWith("VIIPER", StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(file, Path.Combine(AppStaging, Path.GetFileName(file)), true);
            }

            Directory.Delete(controllerStage, true);
        }

        if (Directory.Exists(AppPrevious))
        {
            Directory.Delete(AppPrevious, true);
        }

        Directory.CreateDirectory(_root);
        if (Directory.Exists(App))
        {
            // A process that has only just exited, or a scanner that opened a new file, can hold the
            // folder for a moment. A folder move either happens whole or not at all, so trying again
            // is safe; failing on the first attempt rolled a whole update back.
            MoveWithRetry(App, AppPrevious);
        }

        try
        {
            MoveWithRetry(AppStaging, App);
        }
        catch (IOException)
        {
            if (Directory.Exists(AppPrevious) && !Directory.Exists(App))
            {
                Directory.Move(AppPrevious, App);
            }

            step.Note = "The new WSGM could not replace the installed one; the installed version stays.";
            throw;
        }

        _swapped = true;
        RetireGpuPackages();
        return true;
    }

    private void RetireGpuPackages()
    {
        if (!Directory.Exists(Plugins))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(Plugins, "*.wsgmpkg"))
        {
            if (GpuPackageRetirement.IsRetiredPackage(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void MoveWithRetry(string source, string destination)
    {
        const int attempts = 10;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException ex) when (attempt < attempts)
            {
                SetupLog.Warn($"Moving {source} failed (attempt {attempt} of {attempts}): {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private bool StoreSetup(SetupPayload payload)
    {
        Directory.CreateDirectory(SetupDirectory);
        var self = SetupExecutable.Path;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(SetupExe),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, SetupExe, true);
        }

        var packages = SetupPackages;
        if (Directory.Exists(packages))
        {
            Directory.Delete(packages, true);
        }

        payload.Extract("Packages", packages);
        Directory.CreateDirectory(_machineData);
        File.WriteAllBytes(InstalledBundle, payload.Bundle.ToUtf8Json());
        return true;
    }

    private bool InstallPlugin(SetupPayload payload, BundledPlugin plugin)
    {
        Directory.CreateDirectory(Plugins);
        // Setup owns the ids it bundles: every other build of this id goes, and so does every build of
        // an id it replaced, since two device packages refuse to load. A local build of another id stays.
        foreach (var id in (IEnumerable<string>)[plugin.Id, .. plugin.Replaces])
        {
            foreach (var old in Directory.EnumerateFiles(Plugins, "*.wsgmpkg")
                         .Where(file => IsPackageOf(Path.GetFileName(file), id)))
            {
                File.Delete(old);
            }
        }

        payload.ExtractFile("Packages/" + plugin.File, Path.Combine(Plugins, plugin.File));
        return true;
    }

    private bool ApplyAnswers(SetupStep step, JsonObject answers)
    {
        var file = Path.Combine(Path.GetTempPath(), $"wsgm-answers-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, answers.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return Fail(step, _runtime.Run(AppExe, $"--setup --answers=\"{file}\"") == 0,
                "WSGM could not apply your choices; see wsgm.log.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private bool InstallUsbip(SetupStep step, bool install)
    {
        // The script owns the version comparison, because the script is where the pin lives. A
        // presence check here is deliberately absent: any USBip at all used to satisfy one, which
        // left the reference Claw on a build with known pool corruption for a month (2026-09-26).
        var stage = Path.Combine(Path.GetTempPath(), $"wsgm-controller-{Guid.NewGuid():N}");
        var script = Path.Combine(stage, "Install-UsbipDriver.ps1");
        try
        {
            if (install)
            {
                Registration.CancelResumeAfterRestart();
                Payload!.Extract("Controller", stage);
            }
            else
            {
                // The check needs the script alone, not the 44 MB of installers beside it.
                Payload!.ExtractFile("Controller/Install-UsbipDriver.ps1", script);
            }

            var outcome = RunUsbipScript(script, install ? string.Empty : "-CheckOnly");
            SetupLog.Info("USB/IP: " + outcome.Detail);
            if (outcome.UpdateRequired)
            {
                return PrepareDriverUpdateBoot(step);
            }

            switch (outcome.Outcome)
            {
                case "already-present":
                    step.DoneLabel = "USB/IP driver already up to date";
                    step.State = StepState.Skipped;
                    break;
                case "installed":
                    Components = Components with { Usbip = true };
                    Components.Write(ComponentsFile);
                    break;
            }

            RestartRequired |= outcome.RebootRequired;
            return Fail(step, outcome.Succeeded, outcome.Detail);
        }
        finally
        {
            // The payload or the RunOnce cancellation can fail before the stage folder exists.
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
        }
    }

    /// <summary>Runs the USB/IP script and reads the outcome it publishes.</summary>
    /// <param name="script">The staged script.</param>
    /// <param name="extraArguments">Mode switches, or empty for the real run.</param>
    /// <returns>What the script reported, or a failure when it reported nothing.</returns>
    private UsbipOutcome RunUsbipScript(string script, string extraArguments)
    {
        var status = Path.Combine(_machineData, "usbip-install-status.ini");
        File.Delete(status);
        _runtime.Run(WindowsSetup.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" "
            + $"-StatusPath \"{status}\" {extraArguments}");
        return File.Exists(status)
            ? UsbipOutcome.Parse(File.ReadAllText(status))
            : new UsbipOutcome("failed", true, "The USB/IP driver did not publish a result.");
    }

    /// <summary>
    ///     Turns WSGM's autostart off and schedules the <c>/finishdrivers</c> run, so the driver can be
    ///     replaced on a boot nothing attaches in.
    /// </summary>
    /// <param name="step">The step to label.</param>
    /// <returns>Whether the restart was arranged.</returns>
    /// <remarks>
    ///     usbip-win2 cannot be replaced once something has attached to it this boot (upstream #188),
    ///     and WSGM attaches its pad seconds after sign-in. Disabling the sign-in service is what makes
    ///     the next boot safe; docs/device-integration.md has the account.
    /// </remarks>
    private bool PrepareDriverUpdateBoot(SetupStep step)
    {
        var disabled = WindowsSetup.DisableService();
        DriverUpdatePending = disabled;
        DriverUpdateResumes = disabled && Registration.ScheduleResumeAfterRestart();
        if (!disabled)
        {
            step.DoneLabel = "USB/IP driver update could not be prepared";
            step.State = StepState.Failed;
            step.Note = "WSGM's autostart could not be turned off, so the driver cannot be replaced safely. "
                        + "The installed driver is unchanged.";
            return false;
        }

        step.DoneLabel = "USB/IP driver update prepared; restart to finish it";
        step.State = StepState.Skipped;
        SetupLog.Info($"USB/IP: autostart off, restart pending, resumes on its own={DriverUpdateResumes}.");
        return true;
    }

    private bool InstallHidHide(SetupStep step)
    {
        if (InstalledComponents.HidHidePresent())
        {
            step.DoneLabel = "HidHide already installed";
            step.State = StepState.Skipped;
            return true;
        }

        var stage = Path.Combine(Path.GetTempPath(), $"wsgm-controller-{Guid.NewGuid():N}");
        Payload!.Extract("Controller", stage);
        try
        {
            var installer = Directory.EnumerateFiles(stage, "HidHide*.exe").FirstOrDefault();
            if (installer is null)
            {
                step.Note = "This setup carries no HidHide installer.";
                return false;
            }

            var code = _runtime.Run(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP-");
            if (code == 0)
            {
                Components = Components with { HidHide = true };
                Components.Write(ComponentsFile);
            }

            return Fail(step, code == 0,
                $"HidHide setup exited with code {code}. Steam may show duplicate controllers until it is installed.");
        }
        finally
        {
            Directory.Delete(stage, true);
        }
    }

    private bool Register()
    {
        Registration.Register(ThisVersion.ToString(4));
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        WindowsSetup.CreateShortcut(Path.Combine(programs, "WSGM.lnk"), AppExe, "--shell --activate",
            "Open WSGM");
        WindowsSetup.CreateShortcut(Path.Combine(programs, "WSGM Settings.lnk"), AppExe, "--settings",
            "Configure WSGM");
        return true;
    }

    private bool RestoreController(SetupStep step)
    {
        return RestoreController(step, UninstallExe() ?? AppExe,
            _userData);
    }

    private string? UninstallExe()
    {
        if (_uninstallPrepared)
        {
            return _uninstallExe;
        }

        _uninstallPrepared = true;
        if (File.Exists(AppExe))
        {
            return _uninstallExe = AppExe;
        }

        if (Payload is null)
        {
            return null;
        }

        Payload.Extract("App", AppStaging);
        var app = Path.Combine(AppStaging, "WSGM.exe");
        return _uninstallExe = File.Exists(app) ? app : null;
    }

    /// <summary>
    ///     Puts back what WSGM changed in Steam's folder: its boot-movie override goes and the movie it set
    ///     aside returns, and its <c>themes_custom</c> link goes. WSGM's own rules decide what is WSGM's.
    /// </summary>
    private bool RestoreSteamContent(SetupStep step)
    {
        if (UninstallExe() is not { } app)
        {
            return false;
        }

        var code = _runtime.Run(app, "--restore-steam-content");
        if (code == SessionProtocolNames.SteamStartupMovieStillSetAside)
        {
            // WSGM could not give Steam's own Startup Movie choice back before it closed.
            step.Note = "Choose your startup movie again in Steam: Settings > Customization > Startup Movie.";
            SetupLog.Warn("Uninstall: Steam's own Startup Movie choice is still set aside; "
                          + "choose it again in Steam under Settings > Customization > Startup Movie.");
            return true;
        }

        return code == 0;
    }

    private bool RunUninstallCommand(string arguments, string image = "WSGM.exe")
    {
        return UninstallExe() is { } app
               && _runtime.Run(Path.Combine(Path.GetDirectoryName(app)!, image), arguments) == 0;
    }

    internal bool RestoreController(SetupStep step, string app, string data)
    {
        _controllerRestored = false;
        StillHiddenDevices = ReadLedgerDevices(data);
        if (!File.Exists(app))
        {
            return false;
        }

        var code = _runtime.Run(app, "--uninstall-restore");
        if (code == 0)
        {
            _controllerRestored = true;
            StillHiddenDevices = [];
            return true;
        }

        step.Note = "HidHide did not confirm the change.";
        return false;
    }

    private bool RemoveComponent(SetupStep step, string name, Func<string?> findUninstallCommand)
    {
        if (findUninstallCommand() is not { } command)
        {
            return true;
        }

        return Fail(step,
            _runtime.RunInnoUninstaller(command, () => findUninstallCommand() is not null),
            $"{name} could not be removed; remove it from Windows Settings, Apps.");
    }

    private bool DeleteProgramFiles(SetupStep step)
    {
        _unregister();
        File.Delete(InstalledBundle);
        File.Delete(ComponentsFile);
        File.Delete(PendingPluginRemovals);
        var deleted = DeleteAll(step, [Plugins, App, AppPrevious, AppStaging], _delete);
        SelfDeleteAfterExit();
        return deleted;
    }

    /// <summary>Deletes the user's data, keeping the recovery records while the controller is not restored.</summary>
    /// <param name="step">The step that reports a path that was neither deleted nor scheduled.</param>
    /// <param name="data">The user data folder.</param>
    /// <param name="delete">Deletes one entry now or at the next reboot and says whether either happened.</param>
    /// <returns>Whether every entry was deleted or scheduled.</returns>
    internal bool DeleteUserData(SetupStep step, string data, Func<string, bool> delete)
    {
        if (!Directory.Exists(data))
        {
            return true;
        }

        List<string> entries = [];
        foreach (var entry in Directory.EnumerateFileSystemEntries(data))
        {
            // Failed restoration keeps both controller and Windows recovery records for repair.
            if (!_controllerRestored
                && (string.Equals(Path.GetFileName(entry), "hidhide-ownership.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(entry), "config.json", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            entries.Add(entry);
        }

        return DeleteAll(step, entries, delete);
    }

    // Every path is attempted; the step note names the ones that were neither deleted nor scheduled.
    private static bool DeleteAll(SetupStep step, IEnumerable<string> paths, Func<string, bool> delete)
    {
        List<string> refused = [.. paths.Where(path => !delete(path))];
        return Fail(step, refused.Count == 0,
            "Setup could neither delete these nor schedule them for deletion at the next restart: "
            + string.Join(", ", refused) + ". Delete them by hand.");
    }

    private void SelfDeleteAfterExit()
    {
        var self = SetupExecutable.Path;
        if (!Path.GetFullPath(self).StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            _delete(SetupDirectory);
            TryRemoveEmpty(_root);
            return;
        }

        // A running executable cannot delete itself. A detached PowerShell waits for this process to
        // exit, removes the setup folder, and then the install root only if nothing else is left in it.
        var setup = SetupDirectory.Replace("'", "''", StringComparison.Ordinal);
        var root = _root.Replace("'", "''", StringComparison.Ordinal);
        _runtime.Start(WindowsSetup.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id {Environment.ProcessId} "
            + $"-ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{setup}' -Recurse -Force; "
            + $"[IO.Directory]::Delete('{root}')\"");
    }

    private static void TryRemoveEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
    }

    private void FinishInstall()
    {
        if (_swapped && Directory.Exists(AppPrevious))
        {
            _delete(AppPrevious);
        }

        _owner?.Dispose();
        _owner = null;
    }

    /// <summary>Puts the previous application back and restarts what was running, as Inno's rollback did.</summary>
    private void RollBack()
    {
        try
        {
            if (_shutdownApplied && !_runtime.StopService())
            {
                RollbackIncomplete = true;
                SetupLog.Warn(
                    "Rollback: the sign-in service could not be stopped; the file transaction is kept for repair.");
                _owner?.Dispose();
                _owner = null;
                return;
            }

            if (_files is not null)
            {
                _files.RollBack();
                SetupLog.Info("Rollback: the previous application, plugins and repair metadata are back.");
            }
            else if (_swapped && Directory.Exists(AppPrevious))
            {
                Directory.Delete(App, true);
                Directory.Move(AppPrevious, App);
                SetupLog.Info("Rollback: the previous WSGM is back.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetupLog.Error("Rollback: the previous WSGM could not be restored", ex);
            RollbackIncomplete = true;
            _owner?.Dispose();
            _owner = null;
            return;
        }

        _owner?.Dispose();
        _owner = null;
        if (!_shutdownApplied)
        {
            return;
        }

        if (_service is { Exists: true, Running: true }
            && (!File.Exists(LogonServiceExe) || _runtime.Run(LogonServiceExe, "--install") != 0))
        {
            RollbackIncomplete = true;
            SetupLog.Warn("Rollback: the sign-in service that was running could not be started again.");
        }

        var restart = _runtimeExe is { } previous && File.Exists(previous) ? previous : AppExe;
        if (_runtimeWasRunning && File.Exists(restart))
        {
            SetupLog.Info($"Rollback: restarting {restart}.");
            _runtime.Start(restart, _runtimeWasShell ? "--shell" : "--settings");
        }
        else if (_runtimeWasRunning)
        {
            SetupLog.Warn("Rollback: the WSGM that was running is gone, so it could not be restarted.");
        }
    }

    private static IReadOnlyList<string> ReadLedgerDevices(string data)
    {
        try
        {
            var ledger = Path.Combine(data, "hidhide-ownership.json");
            if (!File.Exists(ledger) || JsonNode.Parse(File.ReadAllText(ledger)) is not JsonObject root
                                     || root["Deltas"] is not JsonArray deltas)
            {
                return [];
            }

            return
            [
                .. deltas.OfType<JsonObject>()
                    .Where(delta => delta["EntryKind"]?.ToString() is "Device" or "1")
                    .Select(delta => delta["Value"]?.ToString())
                    .OfType<string>()
            ];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> InstalledIds(BundleManifest bundle)
    {
        if (!Directory.Exists(Plugins))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(Plugins, "*.wsgmpkg").Select(Path.GetFileName).ToArray();
        return
        [
            .. bundle.Plugins.Where(plugin => files.Any(file =>
                    IsPackageOf(file!, plugin.Id)))
                .Select(plugin => plugin.Id)
        ];
    }

    internal static bool IsPackageOf(string fileName, string id)
    {
        var prefix = id + "-";
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && fileName.Length > prefix.Length
               && char.IsAsciiDigit(fileName[prefix.Length]);
    }

    private static bool Fail(SetupStep step, bool ok, string note)
    {
        if (!ok)
        {
            step.Note = note;
        }

        return ok;
    }
}
