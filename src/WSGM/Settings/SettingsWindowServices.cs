using System;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

// Window lifetime operations are separate from its controls and navigation. ShowTestSheet shows the
// Quick access page's preview sheet over an unsaved snapshot; disposing its result closes it again.
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
    Func<AppConfig, IDisposable> ShowTestSheet)
{
    /// <summary>Builds the production window services around one view model.</summary>
    /// <param name="viewModel">The view model the window edits.</param>
    /// <param name="steamInput">The process's Steam Input lease owner.</param>
    /// <param name="managedPad">The managed controller WSGM's own UI reads, or null when there is none.</param>
    /// <param name="showTestSheet">
    ///     Shows the preview sheet; supplied by the composer, so the window never builds overlay types itself.
    /// </param>
    /// <returns>The window's services.</returns>
    internal static SettingsWindowServices Create(SettingsViewModel viewModel, SteamInputBlocker steamInput,
        ManagedUiPad? managedPad, Func<AppConfig, IDisposable> showTestSheet)
    {
        GamepadService gamepad = new();
        if (managedPad is not null)
        {
            gamepad.UseManagedPad(managedPad);
        }

        return new SettingsWindowServices(gamepad, gamepad.Start, gamepad.Stop,
            SplashTheme.BeginImportSession, SplashTheme.EndImportSession,
            viewModel.RefreshDeviceOwnerStatusAsync, () => viewModel.SavedAccentColor,
            steamInput.Hold, (owner, reason) => steamInput.Drop(owner, reason), showTestSheet);
    }
}
