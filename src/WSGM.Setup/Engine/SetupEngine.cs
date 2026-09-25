using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using WSGM.Install;

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
    Waiting,
    Running,
    Done,
    Failed,
    Skipped
}

/// <summary>One line of the progress page.</summary>
internal sealed class SetupStep(string label, string doneLabel, bool fatal, Func<SetupStep, bool> run)
{
    public string Label { get; } = label;
    public string DoneLabel { get; set; } = doneLabel;
    public bool Fatal { get; } = fatal;
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
internal sealed record UninstallChoices(bool KeepData, bool RemoveUsbip, bool RemoveHidHide);

/// <summary>
///     The install engine. Every step of the Inno installer's <c>[Code]</c>, <c>[Run]</c> and
///     <c>[UninstallRun]</c> maps to a step here, in the same order, with the same refusals.
/// </summary>
internal sealed class SetupEngine : IDisposable
{
    private static readonly string AppStaging = InstallLayout.App + ".staging";
    private static readonly string AppPrevious = InstallLayout.App + ".previous";
    private Mutex? _owner;
    private bool _runtimeCaptured;
    private string? _runtimeExe;
    private bool _runtimeWasRunning;
    private bool _runtimeWasShell;
    private ServiceState _service;
    private bool _shutdownApplied;
    private bool _swapped;

    private SetupEngine(SetupPayload? payload)
    {
        Payload = payload;
    }

    public SetupPayload? Payload { get; }

    public SetupKind Kind { get; private set; }

    public Version? InstalledVersion { get; private set; }

    public Version ThisVersion { get; } =
        typeof(SetupEngine).Assembly.GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : new Version(0, 0);

    /// <summary>The WSGM 1.0 install to remove first, or null.</summary>
    public (string Command, string Version)? Legacy { get; private set; }

    public bool SteamInstalled { get; private set; }

    public PluginOffers? Offers { get; private set; }

    /// <summary>Ids of bundled plugins that have a package file installed now.</summary>
    public IReadOnlyList<string> InstalledPluginIds { get; private set; } = [];

    /// <summary>The answers WSGM exported, or null until <see cref="PrepareAnswers" /> ran.</summary>
    public JsonObject? ExportedAnswers { get; private set; }

    public InstalledComponents Components { get; private set; } = new();

    /// <summary>Whether the last install asked for a restart.</summary>
    public bool RestartRequired { get; private set; }

    /// <summary>Whether the uninstall could not confirm the controller is visible again.</summary>
    public IReadOnlyList<string> StillHiddenDevices { get; private set; } = [];

    public void Dispose()
    {
        _owner?.Dispose();
        Payload?.Dispose();
    }

    /// <summary>Reads the machine: what is installed, the payload, and the offers for this hardware.</summary>
    public static SetupEngine Detect(string? payloadDirectory)
    {
        SetupEngine engine = new(SetupPayload.Open(payloadDirectory));
        engine.SteamInstalled = WindowsSetup.SteamInstalled();
        engine.Legacy = Registration.LegacyInstall();
        engine.InstalledVersion = Registration.InstalledVersion();
        engine.Components = InstalledComponents.Read();
        engine.Kind = engine.InstalledVersion switch
        {
            null => SetupKind.Install,
            { } installed when installed < engine.ThisVersion => SetupKind.Update,
            { } installed when installed > engine.ThisVersion => SetupKind.NewerInstalled,
            _ => SetupKind.Maintain
        };
        if (engine.Payload is { } payload)
        {
            engine.InstalledPluginIds = InstalledIds(payload.Bundle);
            engine.Offers = PluginOffers.Compute(payload.Bundle, DeviceMachineIdentity.Collect(),
                engine.InstalledPluginIds);
        }

        SetupLog.Info($"Setup {engine.ThisVersion}: kind={engine.Kind}, installed={engine.InstalledVersion}, "
                      + $"legacy={engine.Legacy?.Version ?? "none"}, steam={engine.SteamInstalled}, "
                      + $"payload={engine.Payload?.Source ?? "none"}.");
        return engine;
    }

    /// <summary>
    ///     Unpacks the new application beside the installed one and asks it for the current answers, so
    ///     the profile page starts from the user's values, or from the defaults on a fresh install.
    /// </summary>
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
            var code = WindowsSetup.Run(Path.Combine(AppStaging, "WSGM.exe"), $"--export-setup-answers=\"{file}\"");
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

    /// <summary>The system components the chosen plugins need.</summary>
    public IReadOnlyList<SetupComponent> RequiredComponents(InstallChoices choices)
    {
        return Payload?.Bundle.Plugins
            .Where(plugin => plugin.Id == choices.DevicePluginId || choices.CommonPluginIds.Contains(plugin.Id))
            .SelectMany(plugin => SetupComponents.Required(plugin.Capabilities))
            .Distinct()
            .ToArray() ?? [];
    }

    /// <summary>Whether the controller stack must be installed because it is missing.</summary>
    public bool NeedsDrivers(InstallChoices choices)
    {
        return RequiredComponents(choices).Contains(SetupComponent.ControllerStack)
               && (!InstalledComponents.UsbipPresent() || !InstalledComponents.HidHidePresent());
    }

    /// <summary>The steps of an install, update or repair.</summary>
    public IReadOnlyList<SetupStep> PlanInstall(InstallChoices choices)
    {
        var payload = Payload ?? throw new InvalidOperationException("This setup carries no payload.");
        var controller = RequiredComponents(choices).Contains(SetupComponent.ControllerStack);
        List<SetupStep> steps = [];
        if (Legacy is { } legacy)
        {
            steps.Add(new SetupStep("Removing WSGM " + legacy.Version, "WSGM " + legacy.Version + " removed", true,
                step =>
                {
                    // The old uninstaller signals the uninstall event, on which WSGM deliberately leaves Steam
                    // running. Stop WSGM through the update event first, as the old installer's update did,
                    // so WSGM closes Steam gracefully and the mode it ran in is recorded before it is gone.
                    StopAndCapture(false);
                    return Fail(step,
                        Registration.RunInnoUninstaller(legacy.Command, () => Registration.LegacyInstall() is not null),
                        "The WSGM 1.0 uninstaller did not finish. Remove WSGM 1.0 from Windows Settings, then run setup again.");
                }));
        }

        steps.Add(new SetupStep("Closing WSGM and Steam", "WSGM and Steam closed", true,
            step => StopRuntime(step, false)));
        steps.Add(new SetupStep("Copying WSGM", $"WSGM {ThisVersion} installed", true,
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
        steps.Add(new SetupStep("Registering the sign-in service", "Sign-in service registered", true,
            step => Fail(step,
                WindowsSetup.Run(Path.Combine(InstallLayout.App, "WSGM.LogonService.exe"), "--install") == 0,
                "The sign-in service could not be registered; see setup.log.")));
        if (controller)
        {
            steps.Add(new SetupStep("Installing the USB/IP driver", "USB/IP driver installed", false, InstallUsbip)
            {
                Hint = "Your controls drop out for a few seconds now."
            });
            steps.Add(new SetupStep("Installing HidHide", "HidHide installed", false, InstallHidHide));
        }

        steps.Add(new SetupStep("Adding Start menu entries", "Start menu entries added", false, _ => Register()));
        return steps;
    }

    /// <summary>The steps of an uninstall.</summary>
    public IReadOnlyList<SetupStep> PlanUninstall(UninstallChoices choices)
    {
        var app = InstallLayout.AppExe;
        List<SetupStep> steps =
        [
            new("Closing WSGM and Steam", "WSGM and Steam closed", true, step => StopRuntime(step, true)),
            new("Removing the Steam Input shim", "Steam Input shim removed", false,
                _ => !File.Exists(app) || WindowsSetup.Run(app, "--remove-steam-input-shim") == 0),
            new("Removing the sign-in service", "Sign-in service removed", false,
                _ => WindowsSetup.Run(Path.Combine(InstallLayout.App, "WSGM.LogonService.exe"), "--uninstall") == 0),
            new("Restoring the shell registration", "Shell registration restored", false,
                _ => !File.Exists(app) || WindowsSetup.Run(app, "--unregister-shell") == 0),
            new("Showing your controller to games again and restoring Windows settings",
                "Controller shown to games again, Windows settings restored", false, RestoreController)
        ];
        if (choices.RemoveUsbip && Components.Usbip)
        {
            steps.Add(new SetupStep("Removing the USB/IP driver", "USB/IP driver removed", false,
                step => RemoveComponent(step, "USBip"))
            {
                Hint = "Your controls drop out for a few seconds now."
            });
        }

        if (choices.RemoveHidHide && Components.HidHide)
        {
            steps.Add(new SetupStep("Removing HidHide", "HidHide removed", false,
                step => RemoveComponent(step, "HidHide")));
        }

        steps.Add(new SetupStep("Deleting program files", "Program files deleted", false, _ => DeleteProgramFiles()));
        if (!choices.KeepData)
        {
            steps.Add(new SetupStep("Deleting settings and data", "Settings and data deleted", false,
                _ => DeleteUserData()));
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

        FinishInstall();
        return true;
    }

    /// <summary>Starts WSGM the way it was running before, or the session on a fresh install.</summary>
    public void StartWsgm()
    {
        var app = InstallLayout.AppExe;
        if (!File.Exists(app))
        {
            return;
        }

        WindowsSetup.Start(app, _runtimeWasShell ? "--shell" : _runtimeWasRunning ? "" : "--shell --activate");
    }

    private bool StopRuntime(SetupStep step, bool forUninstall)
    {
        if (WindowsSetup.InspectService() is not { } service)
        {
            step.Note = "The WSGM sign-in service state could not be verified, so nothing was stopped.";
            return false;
        }

        _service = service;
        if (!WindowsSetup.StopService())
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
        var existing = File.Exists(InstallLayout.AppExe);
        var includeSteam = existing;
        if (!WindowsSetup.CloseSteam(TimeSpan.FromSeconds(60)) && !existing)
        {
            SetupLog.Warn("Steam stayed open; a fresh install continues, and WSGM starts Steam its own way next time.");
        }

        var blockers = WindowsSetup.Blockers(includeSteam);
        if (blockers.Count > 0)
        {
            step.Note = $"{string.Join(" and ", blockers)} is still running. Close it normally, then run setup again. "
                        + "No process was ended.";
            return false;
        }

        if (forUninstall && File.Exists(Path.Combine(InstallLayout.App, "WSGM.PackagedLaunch.exe"))
                         && WindowsSetup.Run(Path.Combine(InstallLayout.App, "WSGM.PackagedLaunch.exe"), "--recover") !=
                         0)
        {
            step.Note = "An imported Xbox or Store game is still exempt from Windows suspending it, and WSGM could "
                        + "not put it back. packaged-launch.log in %LOCALAPPDATA%\\WSGM names the game.";
            return false;
        }

        _owner = WindowsSetup.ReserveDeviceOwner(TimeSpan.FromSeconds(30));
        if (_owner is null)
        {
            step.Note = "A WSGM or Device Lab hardware owner is still active. Close it and run setup again.";
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Stops WSGM and records, once, how it ran and from where. A later stop sees the temporarily stopped
    ///     state and must not overwrite it, or a rollback would restart nothing or the wrong mode.
    /// </summary>
    private void StopAndCapture(bool forUninstall)
    {
        var wasShell = WindowsSetup.ShellRunning();
        var exe = WindowsSetup.RunningWsgmPath();
        var handoff = forUninstall ? WindowsSetup.StopForUninstall() : WindowsSetup.StopForUpdate();
        if (_runtimeCaptured)
        {
            return;
        }

        _runtimeWasShell = wasShell;
        _runtimeWasRunning = handoff is not ShutdownHandoff.NotRunning || wasShell;
        _runtimeExe = exe;
        _runtimeCaptured = true;
    }

    private bool InstallApplication(SetupStep step, bool controller)
    {
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

        Directory.CreateDirectory(InstallLayout.Root);
        if (Directory.Exists(InstallLayout.App))
        {
            Directory.Move(InstallLayout.App, AppPrevious);
        }

        try
        {
            Directory.Move(AppStaging, InstallLayout.App);
        }
        catch (IOException)
        {
            if (Directory.Exists(AppPrevious) && !Directory.Exists(InstallLayout.App))
            {
                Directory.Move(AppPrevious, InstallLayout.App);
            }

            step.Note = "The new WSGM could not replace the installed one; the installed version stays.";
            throw;
        }

        _swapped = true;
        return true;
    }

    private bool StoreSetup(SetupPayload payload)
    {
        Directory.CreateDirectory(InstallLayout.Setup);
        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstallLayout.SetupExe),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, InstallLayout.SetupExe, true);
        }

        var packages = InstallLayout.SetupPackages;
        if (Directory.Exists(packages))
        {
            Directory.Delete(packages, true);
        }

        payload.Extract("Packages", packages);
        Directory.CreateDirectory(InstallLayout.MachineData);
        File.WriteAllBytes(InstallLayout.InstalledBundle, payload.Bundle.ToUtf8Json());
        return true;
    }

    private static bool InstallPlugin(SetupPayload payload, BundledPlugin plugin)
    {
        Directory.CreateDirectory(InstallLayout.Plugins);
        // Setup owns the ids it bundles: every other build of this id goes, a local build of another
        // id stays.
        foreach (var old in Directory.EnumerateFiles(InstallLayout.Plugins, plugin.Id + "-*.wsgmpkg"))
        {
            File.Delete(old);
        }

        payload.ExtractFile("Packages/" + plugin.File, Path.Combine(InstallLayout.Plugins, plugin.File));
        return true;
    }

    private static bool ApplyAnswers(SetupStep step, JsonObject answers)
    {
        var file = Path.Combine(Path.GetTempPath(), $"wsgm-answers-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, answers.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return Fail(step, WindowsSetup.Run(InstallLayout.AppExe, $"--setup --answers=\"{file}\"") == 0,
                "WSGM could not apply your choices; see wsgm.log.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private bool InstallUsbip(SetupStep step)
    {
        if (InstalledComponents.UsbipPresent())
        {
            step.DoneLabel = "USB/IP driver already installed";
            step.State = StepState.Skipped;
            return true;
        }

        var stage = Path.Combine(Path.GetTempPath(), $"wsgm-controller-{Guid.NewGuid():N}");
        Payload!.Extract("Controller", stage);
        var status = Path.Combine(InstallLayout.MachineData, "usbip-install-status.ini");
        try
        {
            File.Delete(status);
            WindowsSetup.Run(WindowsSetup.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"),
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(stage, "Install-UsbipDriver.ps1")}\" "
                + $"-StatusPath \"{status}\"");
            var outcome = File.Exists(status)
                ? UsbipOutcome.Parse(File.ReadAllText(status))
                : new UsbipOutcome("failed", true, "The USB/IP driver did not publish a result.");
            SetupLog.Info("USB/IP: " + outcome.Detail);
            RestartRequired |= outcome.RebootRequired || !File.Exists(status);
            if (outcome.Outcome == "installed")
            {
                Components = Components with { Usbip = true };
                Components.Write();
            }

            return Fail(step, outcome.Succeeded, outcome.Detail);
        }
        finally
        {
            Directory.Delete(stage, true);
        }
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

            var code = WindowsSetup.Run(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP-");
            if (code == 0)
            {
                Components = Components with { HidHide = true };
                Components.Write();
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
        Registration.Register(ThisVersion.ToString(3));
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        WindowsSetup.CreateShortcut(Path.Combine(programs, "WSGM.lnk"), InstallLayout.AppExe, "--shell --activate",
            "Open WSGM");
        WindowsSetup.CreateShortcut(Path.Combine(programs, "WSGM Settings.lnk"), InstallLayout.AppExe, "--settings",
            "Configure WSGM");
        return true;
    }

    private bool RestoreController(SetupStep step)
    {
        if (!File.Exists(InstallLayout.AppExe))
        {
            return true;
        }

        var code = WindowsSetup.Run(InstallLayout.AppExe, "--uninstall-restore");
        if (code == 0)
        {
            return true;
        }

        StillHiddenDevices = ReadLedgerDevices();
        step.Note = "HidHide did not confirm the change.";
        return false;
    }

    private static bool RemoveComponent(SetupStep step, string displayName)
    {
        if (Registration.FindUninstallCommand(displayName) is not { } command)
        {
            return true;
        }

        return Fail(step,
            Registration.RunInnoUninstaller(command, () => Registration.FindUninstallCommand(displayName) is not null),
            $"{displayName} could not be removed; remove it from Windows Settings, Apps.");
    }

    private bool DeleteProgramFiles()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        foreach (var shortcut in new[] { "WSGM.lnk", "WSGM Settings.lnk" })
        {
            File.Delete(Path.Combine(programs, shortcut));
            File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), shortcut));
        }

        Registration.Unregister();
        File.Delete(InstallLayout.InstalledBundle);
        File.Delete(InstallLayout.InstalledComponents);
        File.Delete(InstallLayout.PendingPluginRemovals);
        foreach (var folder in new[] { InstallLayout.Plugins, InstallLayout.App, AppPrevious, AppStaging })
        {
            WindowsSetup.DeleteOrScheduleAtReboot(folder);
        }

        SelfDeleteAfterExit();
        return true;
    }

    private bool DeleteUserData()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM");
        if (!Directory.Exists(data))
        {
            return true;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(data))
        {
            // An unverified HidHide cleanup keeps its ledger, so a reinstall can finish the job.
            if (StillHiddenDevices.Count > 0
                && string.Equals(Path.GetFileName(entry), "hidhide-ownership.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            WindowsSetup.DeleteOrScheduleAtReboot(entry);
        }

        return true;
    }

    private static void SelfDeleteAfterExit()
    {
        var self = Environment.ProcessPath!;
        if (!Path.GetFullPath(self).StartsWith(InstallLayout.Root, StringComparison.OrdinalIgnoreCase))
        {
            WindowsSetup.DeleteOrScheduleAtReboot(InstallLayout.Setup);
            TryRemoveEmpty(InstallLayout.Root);
            return;
        }

        // A running executable cannot delete itself; a detached shell does once setup has exited.
        WindowsSetup.Start(WindowsSetup.SystemTool("cmd.exe"),
            $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{InstallLayout.Setup}\" & rmdir \"{InstallLayout.Root}\"");
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
            WindowsSetup.DeleteOrScheduleAtReboot(AppPrevious);
        }

        _owner?.Dispose();
        _owner = null;
    }

    /// <summary>Puts the previous application back and restarts what was running, as Inno's rollback did.</summary>
    private void RollBack()
    {
        try
        {
            if (_swapped && Directory.Exists(AppPrevious))
            {
                Directory.Delete(InstallLayout.App, true);
                Directory.Move(AppPrevious, InstallLayout.App);
                SetupLog.Info("Rollback: the previous WSGM is back.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetupLog.Error("Rollback: the previous WSGM could not be restored", ex);
        }

        _owner?.Dispose();
        _owner = null;
        if (!_shutdownApplied)
        {
            return;
        }

        if (_service is { Exists: true, Running: true } &&
            File.Exists(Path.Combine(InstallLayout.App, "WSGM.LogonService.exe")))
        {
            WindowsSetup.Run(Path.Combine(InstallLayout.App, "WSGM.LogonService.exe"), "--install");
        }

        var restart = _runtimeExe is { } previous && File.Exists(previous) ? previous : InstallLayout.AppExe;
        if (_runtimeWasRunning && File.Exists(restart))
        {
            SetupLog.Info($"Rollback: restarting {restart}.");
            WindowsSetup.Start(restart, _runtimeWasShell ? "--shell" : "--settings");
        }
        else if (_runtimeWasRunning)
        {
            SetupLog.Warn("Rollback: the WSGM that was running is gone, so it could not be restarted.");
        }
    }

    private static IReadOnlyList<string> ReadLedgerDevices()
    {
        try
        {
            var ledger = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM",
                "hidhide-ownership.json");
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

    private static IReadOnlyList<string> InstalledIds(BundleManifest bundle)
    {
        if (!Directory.Exists(InstallLayout.Plugins))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(InstallLayout.Plugins, "*.wsgmpkg").Select(Path.GetFileName).ToArray();
        return
        [
            .. bundle.Plugins.Where(plugin => files.Any(file =>
                    file!.StartsWith(plugin.Id + "-", StringComparison.OrdinalIgnoreCase)))
                .Select(plugin => plugin.Id)
        ];
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
