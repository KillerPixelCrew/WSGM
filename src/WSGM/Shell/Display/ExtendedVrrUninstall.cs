using System;
using System.Linq;
using System.Threading.Tasks;
using LibGPUDriverInteract;
using LibHandheld;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The uninstaller's restore of the extended VRR range, when WSGM turned it on and has not turned it off.</summary>
/// <remarks>
///     Runs in the elevated <c>--uninstall-restore</c> one-shot after the resident WSGM has exited. It opens its own
///     Intel driver session, removes WSGM's EDID override and puts the saved Intel state back through the same
///     <see cref="ExtendedVrrService" /> path a user's switch uses, so the panel returns to its factory range after the
///     next start. This runs whatever Device Integration is set to: it removes only what WSGM's own record says WSGM
///     changed. A standalone Atlas8Tweaks install owns the range and is left in place.
/// </remarks>
internal static class ExtendedVrrUninstall
{
    /// <summary>Restores the panel and the Intel state when the configuration holds WSGM's saved original.</summary>
    /// <param name="store">The configuration store.</param>
    /// <returns>Whether nothing is left to restore, and whether a restart loads the factory EDID.</returns>
    internal static async Task<(bool Complete, bool RestartRequired)> RestoreAsync(ConfigStore store)
    {
        ExtendedVrrIntelState? saved;
        try
        {
            saved = store.Read().Config?.ExtendedVrrOriginal;
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: the extended VRR range record could not be read: {ex.Message}");
            return (false, false);
        }

        if (saved is null)
        {
            return (true, false);
        }

        if (HandheldDevice.Detect(HandheldDevice.ReadIdentity())?.ExtendedVrrPanel is not { } panel)
        {
            Log.Warn("Uninstall restore: an extended VRR range record exists, but this machine has no such panel.");
            return (true, false);
        }

        try
        {
            var intel = BuiltinGpuDrivers.All.First(driver => driver.Vendor == GpuVendor.Intel);
            await using var driver = BuiltinGpuDrivers.Create(intel, BuiltinGpuDrivers.StateDirectory(store.Context, intel));
            var health = await driver.StartAsync().ConfigureAwait(false);
            Log.Info($"Uninstall restore: Intel graphics driver session {health} for the extended VRR range.");
            var calls = new GpuFixedRefreshRate(() => [driver], static () => true);
            ExtendedVrrService service = new(() => panel, new WindowsExtendedVrrMachine(), calls,
                () => store.Read().Config?.ExtendedVrrOriginal, original => store.Update(config =>
                {
                    config.ExtendedVrrOriginal = original;
                    return true;
                }), TimeSpan.FromSeconds(30));
            await using (service.ConfigureAwait(false))
            {
                var result = await service.RestoreForUninstallAsync().ConfigureAwait(false);
                Log.Info($"Uninstall restore: extended VRR range {(result.Complete ? "restored" : "not fully restored")}: "
                         + service.Status.Detail);
                await driver.StopAsync().ConfigureAwait(false);
                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Uninstall restore: the extended VRR range could not be restored: {ex.Message}");
            return (false, false);
        }
    }
}
