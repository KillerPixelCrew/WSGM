using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

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

                warn($"{rule.Name} is still running after enforced shutdown.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            warn($"Desktop application exit failed: {ex.Message}");
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
