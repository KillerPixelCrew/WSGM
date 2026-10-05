using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using WSGM.Core;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Settings;

namespace WSGM.Shell;

/// <summary>
///     The resident session's one Settings window. The tray, the overlay and a desktop Settings launch all
///     open it here, so two windows never edit independent drafts of the same configuration.
/// </summary>
internal sealed class SettingsSurface
{
    private readonly Func<ConfigReadResult, SettingsViewModel> _createViewModel;
    private readonly Func<bool> _inGameMode;
    private readonly Func<ManagedUiPad?> _managedPad;
    private readonly SteamInputBlocker _steamInput;
    private readonly ConfigStore _store;
    private Task<ConfigReadResult>? _reading;
    private SettingsWindow? _window;

    /// <summary>Creates the surface; nothing is shown until the first <see cref="OpenAsync" />.</summary>
    /// <param name="createViewModel">Builds the view model over a configuration read and the session's services.</param>
    /// <param name="store">The session's configuration persistence, which the preview sheet reads through.</param>
    /// <param name="steamInput">The process's Steam Input lease owner.</param>
    /// <param name="inGameMode">Whether the session is in game mode now, which decides how the window is listed.</param>
    /// <param name="managedPad">The managed controller WSGM's own UI reads, or null when there is none.</param>
    internal SettingsSurface(Func<ConfigReadResult, SettingsViewModel> createViewModel, ConfigStore store,
        SteamInputBlocker steamInput, Func<bool> inGameMode, Func<ManagedUiPad?> managedPad)
    {
        _createViewModel = createViewModel;
        _store = store;
        _steamInput = steamInput;
        _inGameMode = inGameMode;
        _managedPad = managedPad;
    }

    /// <summary>
    ///     Shows the Settings window, or restores and activates the one already open. In game mode the
    ///     window is listed in the Open apps strip, also when it was first opened on the desktop. Call it on
    ///     the UI thread; a new window reads config.json on a worker first.
    /// </summary>
    /// <returns>The window now on screen.</returns>
    internal async Task<Window> OpenAsync()
    {
        if (_window is null)
        {
            // Requests made while the configuration is being read share that read and the one window.
            _reading ??= Task.Run(() => _store.Read());
            ConfigReadResult read;
            try
            {
                read = await _reading;
            }
            finally
            {
                _reading = null;
            }

            if (_window is null)
            {
                return Create(read);
            }
        }

        var window = _window;
        if (_inGameMode())
        {
            window.IncludeAsSwitchable();
        }

        window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    private SettingsWindow Create(ConfigReadResult read)
    {
        var viewModel = _createViewModel(read);
        var pad = _managedPad();
        var window = new SettingsWindow(viewModel,
            SettingsWindowServices.Create(viewModel, _steamInput, pad, TestSheet(_store, _steamInput, pad)));
        if (_inGameMode())
        {
            window.IncludeAsSwitchable();
        }

        window.Show();
        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        Log.Info($"Settings opened in resident process {Environment.ProcessId}.");
        window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    /// <summary>Closes the window, if one is open. A save in progress finishes first.</summary>
    internal void Close()
    {
        _window?.Close();
    }

    /// <summary>
    ///     The Quick access page's test sheet: the real sheet over an unsaved snapshot, as a preview with no
    ///     session behind it. Each sheet owns its status managers and releases them when it is disposed.
    /// </summary>
    /// <param name="store">The configuration persistence the sheet reads through.</param>
    /// <param name="steamInput">The process's Steam Input lease owner.</param>
    /// <param name="managedPad">The managed controller the sheet's navigation reads, or null.</param>
    /// <returns>Shows one sheet per call; disposing the result closes it.</returns>
    internal static Func<AppConfig, IDisposable> TestSheet(ConfigStore store, SteamInputBlocker steamInput,
        ManagedUiPad? managedPad)
    {
        return config =>
        {
            AudioManager audio = new();
            RadioManager radios = new();
            RemovableDriveManager drives = new();
            OverlayController? sheet = null;
            try
            {
                sheet = new OverlayController(config, store, steamInput, null, new SessionModes(config, null),
                    audio, radios, drives, previewOnly: true, formats: new SdFormatManager(store),
                    activationWindow: null);
                if (managedPad is not null)
                {
                    sheet.UseManagedPad(managedPad);
                }

                sheet.ShowOverlay();
                return new TestSheetLifetime(sheet, audio, radios, drives);
            }
            catch
            {
                new TestSheetLifetime(sheet, audio, radios, drives).Dispose();
                throw;
            }
        };
    }

    private sealed class TestSheetLifetime(
        OverlayController? sheet,
        AudioManager audio,
        RadioManager radios,
        RemovableDriveManager drives) : IDisposable
    {
        public void Dispose()
        {
            // The sheet first: it detaches from the managers it observes before they go.
            sheet?.Dispose();
            audio.Dispose();
            radios.Dispose();
            drives.Dispose();
        }
    }
}
