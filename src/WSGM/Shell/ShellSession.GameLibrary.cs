using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using SteamUiToolkit.Surfaces;
using WSGM.Core;

namespace WSGM.Shell;

public sealed partial class ShellSession
{
    /// <summary>Opens a Game Library page inside Steam for the overlay's hand-off.</summary>
    /// <param name="target">The library page, or one title's artwork page.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Whether Steam took the route.</returns>
    /// <remarks>
    ///     The artwork page renders whatever its source last opened, so the source is opened for the
    ///     title first, exactly as the game menu does before it answers with the route.
    /// </remarks>
    private async Task<bool> OpenGameLibraryInSteamAsync(GameLibrarySteamTarget target,
        CancellationToken cancellationToken)
    {
        if (!_config.Cef.Enabled || _steamUiTransport is not { } transport)
        {
            return false;
        }

        var route = target.Route ?? SteamLibraryImportSurface.Route;
        if (target.ArtworkAppId > 0)
        {
            if (_artwork is null
                || !(await _artwork.OpenAsync(target.ArtworkAppId, target.ArtworkTitle, cancellationToken)
                    .ConfigureAwait(false)).Succeeded)
            {
                return false;
            }

            route = SteamArtworkBrowserSurface.RouteFor(target.ArtworkAppId);
        }

        return await SteamRouteNavigation.NavigateAsync(transport, route, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts packages a killed launcher left exempt back under lifetime management.</summary>
    /// <remarks>
    ///     <para>
    ///         Steam's Stop button terminates the launcher outright, so its own release never runs
    ///         and the package stays exempt. Nothing else ever puts it back: the next launcher only
    ///         sweeps when the user happens to start another imported game, so without this a single
    ///         Stop leaves a package outside lifetime management for the life of the machine.
    ///     </para>
    ///     <para>
    ///         The launcher owns the COM interop for this, so it does the work and WSGM just asks.
    ///         Fire and forget, off the startup path: a sweep that cannot run is not a reason to
    ///         hold up a session.
    ///     </para>
    /// </remarks>
    private static void ReleaseAbandonedPackageExemptions()
    {
        if (PackagedLauncherShortcut.ResolveLauncher() is not { } launcher)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(launcher, "--recover")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                           or ObjectDisposedException or IOException)
            {
                Log.Warn($"Packaged launcher recovery sweep did not run: {ex.Message}");
            }
        });
    }

    /// <summary>Reads every non-Steam shortcut Steam has, with what each one runs, in one call.</summary>
    /// <exception cref="InvalidOperationException">
    ///     The library could not be read whole. Refused, not guessed at: an empty or partial list reads
    ///     as "not one of ours", which turns a generated entry into a fresh Add and a second copy of
    ///     the same game, and a recorded one into a hand-edited conflict.
    /// </exception>
    private static async Task<IReadOnlyList<ExistingShortcut>> ReadShortcutsAsync(
        CancellationToken cancellationToken)
    {
        var listed = await SteamApps.ListShortcutsAsync(cancellationToken).ConfigureAwait(false);
        if (listed is not { Succeeded: true, Shortcuts: { } shortcuts })
        {
            throw new InvalidOperationException(listed.Reachable
                ? $"Steam's library could not be read ({listed.Error}). Nothing was changed; try again."
                : "Steam is not reachable, so its library could not be read. Nothing was changed.");
        }

        return
        [
            .. shortcuts.Select(shortcut =>
                new ExistingShortcut(shortcut.AppId, shortcut.Target, shortcut.LaunchOptions))
        ];
    }

    /// <summary>Reads one shortcut again, right before an apply writes to it.</summary>
    /// <returns>What it runs, or null when Steam has no shortcut with that id any more.</returns>
    /// <exception cref="InvalidOperationException">Steam could not be reached.</exception>
    private static async Task<ExistingShortcut?> ReadShortcutAsync(uint appId, CancellationToken cancellationToken)
    {
        var read = await SteamApps.ReadDetailsAsync(appId, cancellationToken).ConfigureAwait(false);
        if (!read.Reachable)
        {
            throw new InvalidOperationException(
                "Steam is not reachable, so the save stopped. Nothing else was changed.");
        }

        return read.Details is { } details
            ? new ExistingShortcut(appId, details.ShortcutExe, details.ShortcutLaunchOptions)
            : null;
    }

    /// <summary>Creates one shortcut through the toolkit, which confirms which library entry it is.</summary>
    private static async Task<ShortcutWriteResult> AddShortcutAsync(
        string name, ShortcutFields fields, CancellationToken cancellationToken)
    {
        var added = await SteamApps.AddShortcutAsync(
                name, fields.Target, fields.StartDirectory, fields.LaunchOptions, cancellationToken)
            .ConfigureAwait(false);
        return !added.Reachable
            ? new ShortcutWriteResult(0, false, "Steam is not reachable, so nothing was created.")
            : new ShortcutWriteResult(added.AppId, added.Confirmed,
                added.Confirmed ? null : added.Error ?? "Steam did not confirm the new shortcut.", added.Mismatch);
    }
}
