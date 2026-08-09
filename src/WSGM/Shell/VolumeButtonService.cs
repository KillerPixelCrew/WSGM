using System;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>Owns hardware-volume handling while WSGM is the shell. Explorer
/// remains the owner in desktop mode, avoiding double application of a button
/// press when the normal Windows taskbar is present.</summary>
internal sealed class VolumeButtonService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly VolumeIndicator _indicator;
    private bool _gameModeActive;
    private bool _nativeHelperUnavailable;
    private bool _disposed;

    /// <summary>Creates the game-mode volume handler on the Avalonia UI thread.</summary>
    internal VolumeButtonService(MessageWindow window, Func<double> uiScale)
    {
        _window = window;
        _indicator = new VolumeIndicator(uiScale);
        _window.ShellHookReceived += OnShellHook;
    }

    /// <summary>Enables or disables WSGM's replacement-shell volume handling.</summary>
    internal void SetGameModeActive(bool active)
    {
        if (_disposed || _gameModeActive == active)
        {
            return;
        }

        _gameModeActive = active;
        if (active)
        {
            VolumeFeedback.Initialize();
            if (_window.RegisterShellHook())
            {
                Log.Info("Game-mode volume buttons enabled (shell hook + default audio endpoint).");
            }
            else
            {
                Log.Warn("Game-mode volume buttons unavailable: shell-hook registration failed.");
            }
            return;
        }

        _indicator.Hide();
        _window.DeregisterShellHook();
        Log.Info("Game-mode volume buttons disabled; Explorer owns volume commands.");
    }

    private void OnShellHook(nint eventCode, nint data)
    {
        if (!_gameModeActive || eventCode != NativeMethods.HshellAppCommand)
        {
            return;
        }

        var command = VolumeAppCommands.FromShellHookLParam(data);
        if (command == 0)
        {
            return;
        }

        if (_nativeHelperUnavailable)
        {
            return;
        }

        try
        {
            var result = NativeVolumeControl.ApplyCommand(command, out var percentage, out var muted);
            if (result >= 0)
            {
                Log.Info($"Volume button {VolumeAppCommands.Describe(command)} applied to the default audio endpoint " +
                         $"({percentage}%, muted={muted != 0}).");
                VolumeFeedback.Play();
                if (VolumeOsdVisibility.CanShow())
                {
                    _indicator.Show(percentage, muted != 0);
                }
                else
                {
                    _indicator.Hide();
                }
            }
            else
            {
                Log.Warn($"Volume button {VolumeAppCommands.Describe(command)} failed (HRESULT 0x{result:X8}).");
            }
        }
        catch (DllNotFoundException ex)
        {
            DisableForMissingNativeHelper(ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            DisableForMissingNativeHelper(ex);
        }
    }

    private void DisableForMissingNativeHelper(Exception ex)
    {
        _nativeHelperUnavailable = true;
        Log.Error("Game-mode volume buttons disabled: WSGM.VolumeControl.dll is unavailable.", ex);
    }

    /// <summary>Unsubscribes and relinquishes shell-hook ownership.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _window.ShellHookReceived -= OnShellHook;
        _indicator.Dispose();
        if (_gameModeActive)
        {
            _window.DeregisterShellHook();
        }
    }
}
