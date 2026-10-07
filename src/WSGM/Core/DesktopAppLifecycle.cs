using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>A known desktop integration and its application-specific exit protocol.</summary>
/// <param name="Name">Diagnostic product label.</param>
/// <param name="ProcessNames">Primary process names without extensions; never service/helper sweep patterns.</param>
/// <param name="ExitCommand">Sibling executable implementing orderly exit, or null when no such command exists.</param>
/// <param name="ExitArguments">Arguments passed to the exit command.</param>
/// <param name="RestartArguments">Arguments for restoring the captured executable.</param>
/// <param name="CloseMainWindowFirst">Whether the editor window must close before the remaining exit protocol.</param>
/// <param name="CreateNoWindow">Whether restoration should create a hidden process without shell activation.</param>
/// <param name="ExitWindowClass">Optional owned window class receiving WM_CLOSE instead of process termination.</param>
internal sealed record DesktopAppRule(
    string Name,
    string[] ProcessNames,
    string? ExitCommand,
    string ExitArguments,
    string RestartArguments,
    bool CloseMainWindowFirst = false,
    bool CreateNoWindow = false,
    string? ExitWindowClass = null);

/// <summary>An exact current-session process captured before desktop takeover.</summary>
/// <param name="Rule">Exit and restart protocol selected for this process.</param>
/// <param name="ProcessId">Captured PID; must be revalidated with start time and image before stopping.</param>
/// <param name="StartTime">Process creation time in UTC, protecting against PID reuse.</param>
/// <param name="ExecutablePath">Full captured image path used for identity checks and restoration.</param>
/// <param name="Elevated">Whether restoration must retain high integrity rather than use a medium task.</param>
internal sealed record DesktopAppInstance(
    DesktopAppRule Rule,
    int ProcessId,
    DateTime StartTime,
    string ExecutablePath,
    bool Elevated = false);

/// <summary>Process operations kept separate from the session's restoration ownership.</summary>
internal interface IDesktopAppBackend
{
    /// <summary>Captures restorable current-session instances of an explicitly cataloged application.</summary>
    /// <param name="rule">Known process names and exit protocol to inspect.</param>
    /// <returns>Identity snapshots; capture failures propagate rather than losing restoration authority.</returns>
    IReadOnlyList<DesktopAppInstance> Capture(DesktopAppRule rule);
    /// <summary>Revalidates and stops one captured application using its approved exit protocol.</summary>
    /// <param name="instance">Captured identity; PID reuse must not grant authority over a replacement.</param>
    /// <param name="cancellationToken">Cancels waiting; does not undo an already-dispatched exit.</param>
    /// <returns>A task completing when that instance exits; failures or cancellation propagate.</returns>
    Task StopAsync(DesktopAppInstance instance, CancellationToken cancellationToken);
    /// <summary>Checks whether the captured executable is running in the current session.</summary>
    /// <param name="instance">Application rule and executable path to match.</param>
    /// <returns>True for any matching current executable instance, including a replacement PID.</returns>
    bool IsRunning(DesktopAppInstance instance);
    /// <summary>Restores one captured executable while preserving its integrity level.</summary>
    /// <param name="instance">Executable and restart arguments captured before takeover.</param>
    /// <param name="deadline">Absolute UTC budget for restoration and observing launch.</param>
    /// <returns>Dispatch certainty; Unknown must not trigger another launch attempt.</returns>
    Task<ScheduledTaskLaunchDisposition> RestartAsync(DesktopAppInstance instance, DateTimeOffset deadline);
}

/// <summary>
///     Remembers only applications affected by this takeover. The desktop host serializes
///     access and restores them only after a usable desktop exists.
/// </summary>
/// <param name="backend">Process operations; the caller serializes lifecycle access.</param>
/// <param name="warn">Receives incomplete stop/restore diagnostics.</param>
internal sealed class DesktopAppLifecycle(IDesktopAppBackend backend, Action<string> warn)
{
    private readonly List<DesktopAppInstance> _pending = [];

    // Add integrations here, with their primary process names rather than service/helper names.
    // Without an exit command/window, terminate only the captured process tree.
    /// <summary>Explicit integrations suspended for takeover; ordering is reversed for restoration.</summary>
    internal static IReadOnlyList<DesktopAppRule> Rules { get; } =
    [
        new("DisplayFusion", ["DisplayFusion"], "DisplayFusionCommand.exe", "-closeall", ""),
        new("Wallpaper Engine", ["wallpaper32", "wallpaper64"], null, "", "-silent", ExitWindowClass: "WPEEventWindow"),
        new("LittleBigMouse UI", ["LittleBigMouse.Ui.Avalonia"], null, "", "", true),
        new("LittleBigMouse hook", ["LittleBigMouse.Hook"], null, "", "", CreateNoWindow: true)
    ];

    /// <summary>Checks whether a configured executable belongs to a managed desktop integration.</summary>
    /// <param name="path">Executable path or protocol URL.</param>
    /// <returns>True for a case-insensitive catalog basename match; protocols never match.</returns>
    internal static bool MatchesPath(string path)
    {
        if (AppLauncher.IsProtocol(path))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return Rules.Any(rule =>
            rule.ProcessNames.Any(processName => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Captures restoration intent before stopping integrations, then verifies no cataloged instance remains.</summary>
    /// <param name="cancellationToken">Stops further dispatch; cancellation is reported as an unsuccessful stop.</param>
    /// <returns>True only when no cataloged instance remains; false preserves Explorer and pending restoration intent.</returns>
    internal async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        // An unsettled earlier request is not permission to dispatch another stop.
        if (_pending.Count != 0)
        {
            return false;
        }

        List<DesktopAppInstance> captured = [];
        try
        {
            foreach (var rule in Rules)
            {
                captured.AddRange(backend.Capture(rule));
            }

            foreach (var instance in captured)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Remember before dispatch: a timeout can mean the app exited just afterwards.
                _pending.Add(instance);
                await backend.StopAsync(instance, cancellationToken).ConfigureAwait(false);
            }

            foreach (var rule in Rules)
            {
                if (backend.Capture(rule).Count == 0)
                {
                    continue;
                }

                warn($"{rule.Name} is still running; preserving Explorer.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            warn($"Desktop application exit failed; preserving Explorer: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restores pending integrations in reverse order after the owner has a usable desktop.</summary>
    /// <param name="deadline">Absolute UTC restoration budget; unresolved undispatched entries remain pending.</param>
    /// <returns>A task completing after attempts within the budget; logged failures do not fault it.</returns>
    /// <remarks>Accepted or uncertain dispatch retires same-path entries to prevent duplicate launches.</remarks>
    internal async Task RestoreAsync(DateTimeOffset deadline)
    {
        // Start hooks before their supervising UI, which might otherwise spawn another hook.
        var pending = _pending.ToArray();
        Array.Reverse(pending);
        foreach (var instance in pending)
        {
            if (!_pending.Contains(instance))
            {
                continue;
            }

            try
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return;
                }

                if (backend.IsRunning(instance))
                {
                    ForgetExecutable(instance);
                    continue;
                }

                var result = await backend.RestartAsync(instance, deadline)
                    .ConfigureAwait(false);
                if (result is not ScheduledTaskLaunchDisposition.NotDispatched)
                {
                    // Unknown dispatch must not be retried, including on the next desktop return.
                    ForgetExecutable(instance);
                }

                if (result is not ScheduledTaskLaunchDisposition.Dispatched)
                {
                    warn($"Restoring {instance.Rule.Name}: {result}.");
                }
            }
            catch (Exception ex)
            {
                warn($"Restoring {instance.Rule.Name} failed: {ex.Message}");
            }
        }
    }

    private void ForgetExecutable(DesktopAppInstance instance)
    {
        _pending.RemoveAll(candidate => string.Equals(
            candidate.ExecutablePath, instance.ExecutablePath, StringComparison.OrdinalIgnoreCase));
    }
}
