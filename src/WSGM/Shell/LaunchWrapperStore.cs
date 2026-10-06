using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Remembers what a game's launch configuration looked like before WSGM pointed it at the
///     launch wrapper or a custom action, so Remove can restore it.
/// </summary>
internal static class LaunchWrapperStore
{
    /// <summary>Finds a game's pre-wrapper launch configuration.</summary>
    /// <param name="store">The configuration persistence the snapshots live in.</param>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    /// <returns>The snapshot, or <see langword="null" /> if the game has none.</returns>
    internal static Task<LaunchWrapperConfig?> FindAsync(ConfigStore store, long appId,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => (store.Read().Config ?? new AppConfig()).LaunchWrappers.FirstOrDefault(w => w.AppId == appId),
            cancellationToken);
    }

    /// <summary>Records (or updates) a game's pre-wrapper launch configuration.</summary>
    /// <param name="store">The configuration persistence the snapshots live in.</param>
    /// <param name="snapshot">What to remember; replaces any entry for the same game.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task RememberAsync(ConfigStore store, LaunchWrapperConfig snapshot,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => store.Update(config =>
        {
            config.LaunchWrappers.RemoveAll(w => w.AppId == snapshot.AppId);
            config.LaunchWrappers.Add(snapshot);

            return true;
        }), cancellationToken);
    }

    /// <summary>Drops a game's snapshot once its launch configuration is restored.</summary>
    /// <param name="store">The configuration persistence the snapshots live in.</param>
    /// <param name="appId">The Steam app id, or a shortcut's generated id.</param>
    /// <param name="cancellationToken">Cancels the off-thread work.</param>
    internal static Task ForgetAsync(ConfigStore store, long appId, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => store.Update(config =>
            {
                config.LaunchWrappers.RemoveAll(w => w.AppId == appId);
                return true;
            }),
            cancellationToken);
    }
}
