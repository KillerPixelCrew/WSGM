using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What Programs needs from Windows for installed and packaged applications.</summary>
internal interface IInstalledApplicationHost
{
    /// <summary>Lists the applications to offer.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The applications, sorted by name.</returns>
    Task<IReadOnlyList<InstalledProgram>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads application icons.</summary>
    /// <param name="keys">The applications' <see cref="InstalledProgram.Key" /> values, or user model ids.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Each key's caller-owned bitmap, or null where there is none.</returns>
    Task<IReadOnlyDictionary<string, Bitmap?>> ReadIconsAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken);

    /// <summary>Whether a packaged application's package is installed for this user.</summary>
    /// <param name="appUserModelId">The application's user model id.</param>
    /// <returns>True when its package family is registered.</returns>
    bool IsInstalled(string appUserModelId);

    /// <summary>The application user model id a window belongs to.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The id, or null when it has none.</returns>
    string? WindowApplicationId(AppWindow window);

    /// <summary>Asks the de-elevated launcher to activate a packaged application.</summary>
    /// <param name="appUserModelId">The application's user model id.</param>
    /// <param name="cancellationToken">Cancels the dispatch.</param>
    /// <returns>Whether the launch was dispatched; even Dispatched does not prove it started.</returns>
    Task<ScheduledTaskLaunchDisposition> ActivateAsync(string appUserModelId, CancellationToken cancellationToken);
}
