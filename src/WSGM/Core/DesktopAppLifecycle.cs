using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>A known desktop integration and its application-specific exit protocol.</summary>
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
internal sealed record DesktopAppInstance(
    DesktopAppRule Rule,
    int ProcessId,
    DateTime StartTime,
    string ExecutablePath,
    bool Elevated = false);

/// <summary>Process operations kept separate from the session's restoration ownership.</summary>
internal interface IDesktopAppBackend
{
    IReadOnlyList<DesktopAppInstance> Capture(DesktopAppRule rule);
    Task StopAsync(DesktopAppInstance instance, CancellationToken cancellationToken);
    bool IsRunning(DesktopAppInstance instance);
    Task<ScheduledTaskLaunchDisposition> RestartAsync(DesktopAppInstance instance, DateTimeOffset deadline);
}

/// <summary>
///     Remembers only applications affected by this takeover. The desktop host serializes
///     access and restores them only after a usable desktop exists.
/// </summary>
internal sealed class DesktopAppLifecycle(IDesktopAppBackend backend, Action<string> warn)
{
    private readonly List<DesktopAppInstance> _pending = [];

    // Add integrations here, with their primary process names rather than service/helper names.
    // Without an exit command/window, terminate only the captured process tree.
    internal static IReadOnlyList<DesktopAppRule> Rules { get; } =
    [
        new("DisplayFusion", ["DisplayFusion"], "DisplayFusionCommand.exe", "-closeall", ""),
        new("Wallpaper Engine", ["wallpaper32", "wallpaper64"], null, "", "-silent", ExitWindowClass: "WPEEventWindow"),
        new("LittleBigMouse UI", ["LittleBigMouse.Ui.Avalonia"], null, "", "", true),
        new("LittleBigMouse hook", ["LittleBigMouse.Hook"], null, "", "", CreateNoWindow: true)
    ];

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
