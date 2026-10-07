using System;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Input;

namespace WSGM.Settings;

/// <summary>Explicit UI-thread dependencies for one Settings window; the composer retains session-service ownership.</summary>
/// <param name="Gamepad">Window polling service used by navigation and chord recording.</param>
/// <param name="StartInput">Starts this window's polling claim; repeated activation must remain harmless.</param>
/// <param name="StopInput">Stops polling when the window releases its input lifetime.</param>
/// <param name="BeginImportSession">Acquires a counted lease protecting staged splash imports.</param>
/// <param name="EndImportSession">Releases one matching staged-import lease.</param>
/// <param name="RefreshDeviceOwner">Bounded resident-owner status query; does not start device integration.</param>
/// <param name="ReadSavedAccent">Latest committed accent used when abandoning an unsaved preview.</param>
/// <param name="HoldSteamInput">Adds the named input claim without waiting for native acquisition.</param>
/// <param name="DropSteamInput">Ends the named claim with a diagnostic reason; native release is asynchronous.</param>
/// <param name="ShowTestSheet">Opens an unsaved configuration preview; dispose its returned owner to close it.</param>
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
