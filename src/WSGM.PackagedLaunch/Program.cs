using System;
using System.Threading;
using WSGM.Core;

namespace WSGM.PackagedLaunch;

/// <summary>
///     The launcher Steam starts for an imported Xbox, UWP or MSIX game.
/// </summary>
/// <remarks>
///     <para>
///         Steam launches this, Windows activates the packaged game somewhere outside Steam's launch
///         tree, and this process stays alive for the whole session so Steam keeps the shortcut in a
///         running state. It holds the game's processes in a kill-on-close job, so stopping the
///         shortcut in Steam actually stops the game.
///     </para>
///     <para>
///         Composition only: parse, recover, activate, classify, choose a route, supervise, exit.
///         Every decision worth arguing about lives in a type of its own, and the two that decide
///         whether anything is written into the game — <see cref="LaunchRouteSelector" /> and
///         <see cref="GameSessionExitDecision" /> — are pure.
///     </para>
/// </remarks>
internal static class Program
{
    private const int ExitBadArguments = 2;
    private const int ExitActivationFailed = 3;
    private const int ExitRefused = 4;

    // Held for the process lifetime: the delegate is passed to Windows, and letting it be collected
    // would leave a dangling callback for the one event this exists to catch.
    private static NativeMethods.ConsoleCtrlHandler? _consoleHandler;
    private static PackageDebugExemption? _exemption;

    [STAThread]
    internal static int Main(string[] args)
    {
        if (!PackagedLaunchCommand.TryParse(args, out var command, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(PackagedLaunchCommand.Usage);
            PackagedLaunchLog.Error($"Refused the command line: {error}");
            return ExitBadArguments;
        }

        switch (command.Action)
        {
            case PackagedLaunchAction.Help:
                Console.WriteLine(PackagedLaunchCommand.Usage);
                return 0;
            case PackagedLaunchAction.Recover:
                return Recover();
            default:
                return Launch(command.Request!);
        }
    }

    /// <summary>Releases package exemptions left behind by a launcher that was killed.</summary>
    private static int Recover()
    {
        var released = PackageDebugExemption.ReleaseAbandoned(Journal());
        PackagedLaunchLog.Info(released == 0
            ? "Package lifetime recovery: nothing to release."
            : $"Package lifetime recovery: released {released} stale exemption(s).");
        return 0;
    }

    private static int Launch(PackagedLaunchRequest request)
    {
        PackagedLaunchLog.Info(
            $"Launching {request.Aumid} as {(request.Mode is PackagedLaunchMode.SteamOverlay
                ? "steam-overlay"
                : "controller-only")}"
            + $"{(request.Multiplayer ? ", marked multiplayer" : "")}"
            + $"{(request.AcknowledgedBanRisk ? ", ban risk acknowledged" : "")}, "
            + $"{PrivilegeJournal.Elevation()}.");

        // A console under Big Picture steals the foreground, and foreground attribution is what
        // Steam Input follows. The subsystem stays Console because a windowless wrapper is treated
        // by Steam as a game and hooked; the window is simply hidden once the log is open.
        HideConsole();

        // Before activating anything: a previous launcher that Steam killed may have left this or
        // another package permanently exempt from lifetime management.
        PackageDebugExemption.ReleaseAbandoned(Journal());

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var activation = PackageActivation.Activate(request.Aumid, request.GameArguments);
        PackagedLaunchLog.Info(activation.Detail);
        if (!activation.Succeeded)
        {
            PackagedLaunchLog.Error(
                "Activation failed, so this wrapper is exiting rather than leaving Steam showing a "
                + "game that never started.");
            return ExitActivationFailed;
        }

        var seed = activation.SeedProcessId > 0
            ? ProcessInspector.Describe(activation.SeedProcessId)
            : null;
        var packageFullName = activation.SeedProcessId > 0
            ? PackageIdentity.FullNameOf(activation.SeedProcessId)
            : null;
        packageFullName ??= PackageIdentity.ResolveFullName(request.PackageFamilyName);

        var installPath = packageFullName is null ? null : PackageIdentity.InstallPathOf(packageFullName);
        var (runtime, evidence) = seed is null
            ? (PackagedRuntime.Unknown, "Activation returned no process to classify.")
            : PackageIdentity.Classify(seed, installPath);
        PackagedLaunchLog.Info($"Runtime: {runtime}. {evidence}");

        var decision = LaunchRouteSelector.Select(Requested(request.Mode), runtime);
        PackagedLaunchLog.Info($"Route: {decision.Route}. {decision.Reason}");

        using GameSessionJob job = new();
        using PackageDebugExemption exemption = new(Journal());
        _exemption = exemption;
        InstallShutdownHandlers();

        // After activation on purpose: IPackageDebugSettings takes a running package out of
        // lifetime management, and nothing can suspend a game in the seconds before this runs.
        if (packageFullName is { } fullName)
        {
            exemption.Request(fullName, Environment.ProcessId,
                ProcessInspector.StartedAt(Environment.ProcessId));
        }
        else
        {
            PackagedLaunchLog.Warn(
                $"No installed package matches {request.PackageFamilyName}, so it cannot be exempted "
                + "from lifetime management and may be suspended when it loses the foreground.");
        }

        PrivilegeJournal privileges = new();
        GameInjector injector = new(privileges);
        var degraded = decision.Degraded;

        // Before the game exists. The helper activation returned is the only place Steam's handoff
        // can still be influenced; once the game is running it is too late for this route.
        if (decision.Route is LaunchRoute.PackagedWin32Overlay)
        {
            var prepared = new PackagedWin32OverlayRoute(injector).Prepare(activation.SeedProcessId);
            PackagedLaunchLog.Info(prepared.Detail);
            if (!prepared.Succeeded)
            {
                // The game still launches. The user asked to play, and Steam's running state and
                // containment are worth keeping even when the overlay is not.
                degraded = true;
            }
        }

        AppContainerOverlayRoute? appContainer = null;
        GameForegroundProxy? proxy = null;
        if (decision.Route is LaunchRoute.AppContainerOverlay)
        {
            appContainer = new AppContainerOverlayRoute(injector);
            var prepared = appContainer.Prepare(activation.SeedProcessId, request.Diagnostics);
            PackagedLaunchLog.Info(prepared.Detail);
            if (prepared.Succeeded)
            {
                // Only for this route. The frame a UWP title renders into belongs to
                // ApplicationFrameHost, and Steam Input follows the foreground window's owner.
                proxy = new GameForegroundProxy();
                proxy.SetTarget(activation.SeedProcessId);
            }
            else
            {
                degraded = true;
            }
        }

        var reported = false;
        var rendererChecked = false;

        // Declared before it is built so the observation callback can mark the session degraded:
        // the callback only ever runs from inside Run, long after the assignment.
        GameSessionSupervisor? supervisor = null;
        supervisor = new GameSessionSupervisor(request.PackageFamilyName, job, facts =>
        {
            if (request.ReportPrivileges && !reported)
            {
                reported = true;
                PrivilegeJournal.ReportAccess(facts.Id);
            }

            // Observation, not repair. Whether Steam's handoff reached the process that owns the
            // swap chain is the one thing this route cannot assume, and a game without the renderer
            // is reported rather than written to.
            // The one reconciliation SetTarget does can land before the game's CoreWindow is inside
            // the foreground frame, and attaching it raises no further foreground event. Without
            // this, Steam Input keeps following ApplicationFrameHost until the user alt-tabs.
            if (decision.Route is LaunchRoute.AppContainerOverlay)
            {
                proxy?.SetTarget(facts.Id);
            }

            if (decision.Route is LaunchRoute.PackagedWin32Overlay && !rendererChecked
                                                                   && !GameSessionJob.IsLaunchHelper(facts))
            {
                rendererChecked = true;
                if (PackagedWin32OverlayRoute.RendererReached(facts))
                {
                    PackagedLaunchLog.Info(
                        $"Steam's renderer is present in {facts.Name} ({facts.Id}).");
                }
                else
                {
                    PackagedLaunchLog.Warn(
                        $"Steam's renderer is not in {facts.Name} ({facts.Id}). The overlay will not "
                        + "draw for this title, and nothing is injected to force it.");
                    supervisor!.Degraded = true;
                }
            }
        })
        {
            Degraded = degraded
        };

        int outcomeCode;
        try
        {
            var outcome = supervisor.Run(activation.SeedProcessId, cancellation.Token);
            if (outcome is GameSessionOutcome.Cancelled)
            {
                // The job is kill-on-close and this method is about to close it. A cancellation
                // says the game is left running, so the containment has to be let go first.
                job.Abandon();
            }

            // While the game is still alive: the bridge's own counters live in its address space.
            if (outcome is not GameSessionOutcome.Completed and not GameSessionOutcome.Degraded)
            {
                appContainer?.Report(activation.SeedProcessId);
            }

            PackagedLaunchLog.Info(Describe(outcome));
            outcomeCode = GameSessionExitDecision.ExitCode(outcome);
        }
        finally
        {
            proxy?.Dispose();
            appContainer?.Dispose();
        }

        return outcomeCode;
    }

    /// <summary>Maps the shortcut's vocabulary to the launcher's own.</summary>
    /// <remarks>Exhaustive on purpose: a mode added to the contract stops this compiling.</remarks>
    private static RequestedInputMode Requested(PackagedLaunchMode mode)
    {
        return mode switch
        {
            PackagedLaunchMode.SteamOverlay => RequestedInputMode.SteamOverlay,
            PackagedLaunchMode.ControllerOnly => RequestedInputMode.ControllerOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown launch mode.")
        };
    }

    private static string Describe(GameSessionOutcome outcome)
    {
        return outcome switch
        {
            GameSessionOutcome.Completed => "The game exited; releasing Steam's running state.",
            GameSessionOutcome.Degraded =>
                "The game exited. The session ran without everything that was asked of it.",
            GameSessionOutcome.NeverAppeared =>
                "No process carrying this package appeared, so there is nothing to supervise.",
            GameSessionOutcome.Cancelled => "Stop requested; leaving the game running and exiting.",
            _ => "The session ended."
        };
    }

    private static PackageDebugRecoveryRecord Journal()
    {
        return new PackageDebugRecoveryRecord(
            PackageDebugRecoveryRecord.DefaultPath,
            static (processId, startedUtc) =>
            {
                if (processId == Environment.ProcessId)
                {
                    return true;
                }

                var started = ProcessInspector.StartedAt(processId);
                if (started is null)
                {
                    return false;
                }

                // A live process id proves nothing on its own: Windows reuses them, and the record
                // is only this process's if it started when the record says it did.
                return startedUtc is null
                       || Math.Abs((started.Value - startedUtc.Value).TotalSeconds) < 2;
            });
    }

    private static void HideConsole()
    {
        var window = NativeMethods.GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ShowWindow(window, NativeMethods.SwHide);
        PackagedLaunchLog.SuppressConsole();
    }

    /// <summary>Releases the package exemption on the shutdowns Windows lets a console process see.</summary>
    /// <remarks>
    ///     Best effort, and explicitly not the mechanism the design relies on: a TerminateProcess
    ///     from Steam runs none of this, which is exactly why the recovery journal exists.
    /// </remarks>
    private static void InstallShutdownHandlers()
    {
        _consoleHandler = _ =>
        {
            _exemption?.Dispose();
            _exemption = null;
            return false;
        };
        NativeMethods.SetConsoleCtrlHandler(_consoleHandler, true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _exemption?.Dispose();
            _exemption = null;
        };
    }
}
