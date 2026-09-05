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
    Func<string> ReadSavedAccent)
{
    internal static SettingsWindowServices Create(SettingsViewModel viewModel)
    {
        GamepadService gamepad = new();
        return new(gamepad, gamepad.Start, gamepad.Stop,
            SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
            viewModel.RefreshDeviceOwnerStatusAsync, () => ConfigStore.Load().AccentColor);
    }
}
