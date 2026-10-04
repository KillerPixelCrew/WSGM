using System;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

// Window lifetime operations are separate from its controls and navigation.
internal sealed record SettingsWindowServices(
    GamepadService Gamepad,
    Action StartInput,
    Action StopInput,
    Action BeginImportSession,
    Action EndImportSession,
    Func<Task> RefreshDeviceOwner,
    Func<string> ReadSavedAccent,
    Action<string> HoldSteamInput,
    Action<string, string> DropSteamInput,
    ManagedUiPad? ManagedPad = null)
{
    internal static SettingsWindowServices Create(SettingsViewModel viewModel, ManagedUiPad? managedPad = null)
    {
        GamepadService gamepad = new();
        if (managedPad is not null)
        {
            gamepad.UseManagedPad(managedPad);
        }
        return new SettingsWindowServices(gamepad, gamepad.Start, gamepad.Stop,
            SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
            viewModel.RefreshDeviceOwnerStatusAsync, () => (viewModel.Store.Read().Config ?? new AppConfig()).AccentColor,
            SteamInputBlocker.Hold, (owner, reason) => SteamInputBlocker.Drop(owner, reason), managedPad);
    }
}
