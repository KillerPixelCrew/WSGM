using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Interop;
using WSGM.Shared;

namespace WSGM.Core;

public sealed partial class EmulatorManager
{
    /// <summary>Runs the explicitly requested, verified Microsoft runtime setup for an emulator.</summary>
    /// <param name="definitionId">The reviewed emulator definition.</param>
    /// <param name="cancellationToken">Cancels acquisition or waiting; never terminates Microsoft setup.</param>
    public Task InstallRuntimeDependenciesAsync(string definitionId, CancellationToken cancellationToken)
    {
        return OperationAsync(async token =>
        {
            var definition = Definition(definitionId);
            var architecture = Architecture(definition);
            if (!definition.DataPolicy.RequiresVisualCpp && definition.Id != "model2")
            {
                throw new InvalidOperationException("This emulator has no separately installed Microsoft runtime.");
            }

            foreach (var installed in GetSnapshot().Installations)
            {
                if (installed.DefinitionId == definitionId)
                {
                    EmulatorPrerequisites.EnsureStopped(installed);
                }
            }

            var runtimeCheck = GetSnapshot().Installations.FirstOrDefault(installed =>
                installed.DefinitionId == definitionId && installed.Architecture == architecture)
                ?? new EmulatorInstallation
                {
                    DefinitionId = definition.Id, Architecture = architecture,
                    ExecutablePath = Path.Combine(_context.Root, "Emulation", "runtime-check.exe"),
                    DataPolicy = definition.DataPolicy
                };
            runtimeCheck = runtimeCheck with { DataPolicy = definition.DataPolicy };
            if (EmulatorStorage.MissingRuntimePrerequisites(runtimeCheck).Length == 0)
            {
                Admission(token, () => Mutate(store => store with
                {
                    Installations = store.Installations.Select(EmulatorPrerequisites.RefreshState).ToArray()
                }));
                SetStatus("The required Microsoft runtime is already available.");
                return;
            }

            var root = Path.Combine(_context.Root, "Emulation", "runtime-setup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var setupRunning = false;
            try
            {
                var reboot = false;
                if (definition.DataPolicy.RequiresVisualCpp)
                {
                    var file = "vc_redist." + architecture + ".exe";
                    reboot |= await InstallAsync(file, "https://aka.ms/vc14/" + file,
                        "/install /quiet /norestart", "Microsoft Visual C++", token).ConfigureAwait(false);
                }

                if (definition.Id == "model2")
                {
                    reboot |= await InstallAsync("dxwebsetup.exe",
                        "https://download.microsoft.com/download/1/7/1/1718ccc4-6315-4d8e-9543-8e28a4e18c4c/dxwebsetup.exe",
                        "/Q", "Microsoft DirectX legacy components", token).ConfigureAwait(false);
                }

                var missing = EmulatorStorage.MissingRuntimePrerequisites(runtimeCheck);
                if (!reboot && missing.Length > 0)
                {
                    throw new InvalidOperationException("Microsoft setup finished, but the required runtime is still unavailable. "
                        + string.Join(" ", missing));
                }

                Admission(token, () => Mutate(store => store with
                {
                    Installations = store.Installations.Select(EmulatorPrerequisites.RefreshState).ToArray()
                }));
                SetStatus(reboot
                    ? "Microsoft setup finished and requested a Windows restart. Saves and emulator settings are unchanged."
                    : "Microsoft runtime setup finished. Install or repair the emulator to check its startup.");

                async Task<bool> InstallAsync(string name, string url, string arguments, string label,
                    CancellationToken cancellation)
                {
                    var processName = Path.GetFileNameWithoutExtension(name);
                    var running = Process.GetProcessesByName(processName);
                    try
                    {
                        if (running.Length > 0)
                        {
                            throw new InvalidOperationException(label +
                                " setup is already running. Finish it before starting another setup.");
                        }
                    }
                    finally
                    {
                        foreach (var process in running)
                        {
                            process.Dispose();
                        }
                    }

                    SetStatus("Downloading " + label + " from Microsoft");
                    var installer = Path.Combine(root, name);
                    await _packages.DownloadAsync(new EmulatorAsset(name, url), installer, cancellation)
                        .ConfigureAwait(false);
                    using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (NativeAuthenticode.VerifyFile(installer) != 0)
                    {
                        throw new InvalidDataException(label + " setup did not pass Windows signature verification.");
                    }

#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no X509CertificateLoader equivalent.
                    using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(installer));
#pragma warning restore SYSLIB0057
                    if (!signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false)
                            .Equals("Microsoft Corporation", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Runtime setup is not signed by Microsoft Corporation.");
                    }

                    cancellation.ThrowIfCancellationRequested();
                    SetStatus("Installing " + label + ". Accept the Windows permission prompt if shown.");
                    Process? started;
                    try
                    {
                        started = Process.Start(new ProcessStartInfo(installer)
                        {
                            Arguments = arguments, WorkingDirectory = root, UseShellExecute = true, Verb = "runas"
                        });
                    }
                    catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
                    {
                        throw new InvalidOperationException("Microsoft runtime setup was cancelled at the Windows permission prompt.");
                    }

                    using var setup = started ?? throw new IOException("Microsoft runtime setup could not be started.");
                    setupRunning = true;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    wait.CancelAfter(TimeSpan.FromMinutes(10));
                    try
                    {
                        await setup.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new InvalidOperationException("WSGM stopped waiting for Microsoft setup. Finish the setup window before trying again; its installer has not been terminated.");
                    }

                    setupRunning = false;
                    if (setup.ExitCode is not (0 or SessionProtocolNames.RestartRequired))
                    {
                        throw new InvalidOperationException(label + " setup exited with code " + setup.ExitCode +
                            ". Check the Microsoft setup result before trying again.");
                    }

                    return setup.ExitCode == SessionProtocolNames.RestartRequired;
                }
            }
            finally
            {
                // After uncertain dispatch keep the signed setup file for the still-running
                // Microsoft process. Never delete or kill its work on WSGM cancellation.
                if (!setupRunning)
                {
                    try
                    {
                        EmulatorPackages.DeleteOwned(root, Path.Combine(_context.Root, "Emulation", "runtime-setup"));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn("Microsoft runtime setup files could not be removed: " + exception.Message);
                    }
                }
            }
        }, cancellationToken);
    }
}
