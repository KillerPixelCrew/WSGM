using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Runs the configured desktop startup and wake action lists, one run at a time, only on a settled desktop.</summary>
/// <param name="state">The session's live state, whose configuration supplies the lists.</param>
/// <param name="integrations">The plugins that run the actions.</param>
/// <param name="devicePower">The device power queue a wake action waits behind.</param>
/// <param name="modes">Reads the session's mode coordinator.</param>
internal sealed class DesktopLifecycleActions(
    SessionState state,
    IntegrationServices integrations,
    DevicePowerQueue devicePower,
    Func<SessionModes?> modes)
{
    private readonly DesktopActionAdmission _admission = new();

    /// <summary>Runs the desktop startup or wake action list, coalesced.</summary>
    /// <param name="startup">True for the startup list, false for the wake list.</param>
    /// <remarks>
    ///     The work starts synchronously inside the posted callback and handles its own failures;
    ///     the admission refuses it once the session is stopping.
    /// </remarks>
    internal void Queue(bool startup)
    {
        Dispatcher.UIThread.Post(() => _ = RunAsync(startup));
    }

    private async Task RunAsync(bool startup)
    {
        // The session's current configuration, which a reload keeps at the last good read, rather
        // than a second load of the file. The admission admits one run at a time.
        var steps = startup
            ? state.Config.GameModeLaunch.DesktopStartupActions
            : state.Config.GameModeLaunch.DesktopWakeActions;
        if (steps.Count == 0 || state.ShutdownRequested || state.OverlayTestOnly || !_admission.TryBegin(
                state.InGameMode, modes()?.TransitionInProgress != false, Environment.TickCount64))
        {
            return;
        }

        try
        {
            await integrations.CommonPluginStartup.WaitAsync(state.ShutdownToken);
            await devicePower.Work.WaitAsync(state.ShutdownToken);
            // Re-checked after both waits: a Game Mode entry can have started meanwhile, and a
            // desktop action list must never fire into a session that is leaving the desktop.
            if (state.ShutdownRequested || state.InGameMode || modes()?.TransitionInProgress != false)
            {
                return;
            }

            foreach (var step in await integrations.ActionSequence().RunAllAsync(steps, state.ShutdownToken))
            {
                Log.Info($"Desktop {(startup ? "startup" : "wake")} action "
                         + $"{step.Step.Plugin?.PluginId}/{step.Step.Plugin?.InstanceId}:{step.Step.ActionId}: "
                         + $"{step.Outcome}: {step.Detail}");
                if (!step.Succeeded)
                {
                    modes()?.ReportWarning($"Desktop action: {step.Detail}");
                }
            }
        }
        catch (OperationCanceledException) when (state.ShutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Desktop lifecycle actions failed", ex);
        }
        finally
        {
            _admission.End();
        }
    }
}
