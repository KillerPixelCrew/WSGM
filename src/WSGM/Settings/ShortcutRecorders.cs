using System;
using WSGM.Input;

namespace WSGM.Settings;

/// <summary>Owns the Settings window's two shortcut recorders (keyboard hotkey +
/// controller chord). The bodies moved verbatim from the window's code-behind:
/// the 200 ms delay keeps the press that STARTED recording out of the recording,
/// and the closed re-check after that delay prevents installing a low-level
/// keyboard hook with nothing left to dispose it. The window creates one
/// instance and disposes it in the same slot of its Closed handler that used to
/// dispose the two recorders directly (key recorder first, chord second).</summary>
internal sealed class ShortcutRecorders : IDisposable
{
    private readonly SettingsViewModel _viewModel;
    private readonly Func<bool> _closed;
    private KeyRecorder? _keyRecorder;
    private GamepadChordRecorder? _chordRecorder;

    /// <summary>Binds the recorders to the window's view model and closed flag.</summary>
    /// <param name="viewModel">The view model recorded shortcuts are applied to.</param>
    /// <param name="closed">Returns whether the owning window has closed — re-checked
    /// after the 200 ms arming delay before any hook is installed.</param>
    internal ShortcutRecorders(SettingsViewModel viewModel, Func<bool> closed)
    {
        _viewModel = viewModel;
        _closed = closed;
    }

    /// <summary>Arms keyboard-shortcut recording (200 ms delayed, closed-window safe).</summary>
    internal async void RecordHotkey()
    {
        // Small delay so the key/controller press that started recording (Enter, A)
        // isn't the thing we record — same trick Handheld Companion uses.
        _viewModel.SetHotkeyRecording(true);
        await System.Threading.Tasks.Task.Delay(200);
        if (_closed())
        {
            // Window closed during the delay: creating the recorder now would
            // install a low-level keyboard hook with nothing left to dispose it.
            return;
        }

        _keyRecorder?.Dispose();
        _keyRecorder = new KeyRecorder();
        _keyRecorder.Recorded += (modifiers, vk) =>
        {
            _viewModel.ApplyRecordedHotkey(modifiers, vk);
            _keyRecorder?.Dispose();
            _keyRecorder = null;
        };
        _keyRecorder.Start();
    }

    /// <summary>Stops any active hotkey recording and clears the stored shortcut.</summary>
    internal void ClearHotkey()
    {
        _keyRecorder?.Dispose();
        _keyRecorder = null;
        _viewModel.ClearHotkey();
    }

    /// <summary>Arms controller-chord recording (200 ms delayed, closed-window safe).</summary>
    internal async void RecordChord()
    {
        _viewModel.SetChordRecording(true);
        await System.Threading.Tasks.Task.Delay(200);
        if (_closed())
        {
            // Same race as RecordHotkey: no recorder after the window is gone.
            return;
        }

        _chordRecorder?.Dispose();
        _chordRecorder = new GamepadChordRecorder();
        _chordRecorder.Recorded += (buttons, hold) =>
        {
            _viewModel.ApplyRecordedChord(buttons, hold);
            _chordRecorder?.Dispose();
            _chordRecorder = null;
        };
        _chordRecorder.Start();
    }

    /// <summary>Stops any active chord recording and clears the stored chord.</summary>
    internal void ClearChord()
    {
        _chordRecorder?.Dispose();
        _chordRecorder = null;
        _viewModel.ClearChord();
    }

    /// <summary>Disposes both recorders in the historical order: key recorder
    /// first, chord recorder second.</summary>
    public void Dispose()
    {
        _keyRecorder?.Dispose();
        _keyRecorder = null;
        _chordRecorder?.Dispose();
        _chordRecorder = null;
    }
}
