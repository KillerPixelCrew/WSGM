using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>
///     Owns hardware-volume handling while WSGM is the shell. Explorer
///     remains the owner in desktop mode, avoiding double application of a button
///     press when the normal Windows taskbar is present.
/// </summary>
internal sealed class VolumeButtonService : IDisposable
{
    private readonly AudioManager _audio;
    private readonly MessageWindow _window;
    private Task _commands = Task.CompletedTask;
    // Read by queued commands off the UI thread, which drop themselves once handling is off or disposed.
    private volatile bool _disposed;
    private volatile bool _gameModeActive;

    /// <summary>Creates the game-mode volume handler on the Avalonia UI thread.</summary>
    /// <param name="window">The process message-only window carrying the session's Game Mode shell hook.</param>
    /// <param name="audio">
    ///     The session's audio state owner, told about every volume
    ///     this service writes so the taskbar slider does not lag the button by a poll.
    /// </param>
    internal VolumeButtonService(MessageWindow window, AudioManager audio)
    {
        _window = window;
        _audio = audio;
        _window.ShellHookReceived += OnShellHook;
    }

    /// <summary>Unsubscribes from the shell hook.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.ShellHookReceived -= OnShellHook;
    }

    /// <summary>Enables or disables WSGM's replacement-shell volume handling.</summary>
    /// <param name="active">
    ///     Whether WSGM is the shell. The session registers the shell hook for Game Mode; this only
    ///     decides whether its app commands are handled. Invoke on the UI thread; disposed or
    ///     unchanged state is ignored.
    /// </param>
    internal void SetGameModeActive(bool active)
    {
        if (_disposed || _gameModeActive == active)
        {
            return;
        }

        _gameModeActive = active;
        // VolumeFeedback is preopened by AudioManager.Start, which the session
        // runs before this service exists; Play() self-initializes as backstop.
        Log.Info(active
            ? "Game-mode volume buttons enabled (shell hook + default audio endpoint)."
            : "Game-mode volume buttons disabled; Explorer owns volume commands.");
    }

    private void OnShellHook(nint eventCode, nint data)
    {
        if (!_gameModeActive || eventCode != NativeMethods.HshellAppCommand)
        {
            return;
        }

        if (VolumeAppCommands.FromShellHookLParam(data) is not { } command)
        {
            return;
        }

        var previous = _commands;
        _commands = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            if (_gameModeActive && !_disposed)
            {
                ApplyCommand(command);
            }
        });
    }

    private void ApplyCommand(CoreAudio.VolumeCommand command)
    {
        try
        {
            var result = CoreAudio.ApplyCommand(command, out var percentage, out var muted);
            if (result >= 0)
            {
                Log.Info($"Volume button {command} applied to the default audio endpoint " +
                         $"({percentage}%, muted={muted != 0}).");
                // The write already happened above; hand the landed state to the audio owner so the
                // taskbar slider matches at once. Steam's own volume display shows the change.
                VolumeFeedback.Play();
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed || !_gameModeActive)
                    {
                        return;
                    }

                    _audio.NoteExternalVolume(percentage, muted != 0);
                });
            }
            else
            {
                Log.Warn($"Volume button {command} failed (HRESULT 0x{result:X8}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Volume button {command} failed unexpectedly.", ex);
        }
    }
}
