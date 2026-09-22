using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Answers Steam's Use global action on a row whose value the running game overrides.</summary>
/// <remarks>
///     Removes only the game's override, so the value falls back to Global. The profile owner's fan-out
///     then applies it and the row republishes without its marker.
/// </remarks>
/// <param name="profiles">The profile owner.</param>
/// <param name="deviceIdentityKey">The machine's device identity, for a device capability's override.</param>
internal sealed class NativeQamProfileOverrideService(
    ProfileService profiles,
    Func<string?> deviceIdentityKey) : ISteamProfileOverrideBackend
{
    public async Task<SteamUiCommandResult> UseGlobalAsync(string settingId, CancellationToken cancellationToken)
    {
        if (!ProfileSettingKey.TryParse(settingId, out var key))
        {
            return new SteamUiCommandResult(false, "The setting is not one a game profile can override.");
        }

        var cleared = await profiles.ClearGameOverrideAsync(
            key,
            key.Field is ProfileField.Device ? deviceIdentityKey() : null,
            cancellationToken).ConfigureAwait(false);
        return cleared
            ? new SteamUiCommandResult(true, null)
            : new SteamUiCommandResult(false, "The running game does not override this setting.");
    }
}
