using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Writes one data flow's default endpoint for <see cref="AudioManager" />. Writes are serialized,
///     a stale queued request is skipped, and an already-running stale request cannot publish UI state
///     after the user's newer choice.
/// </summary>
internal sealed class AudioEndpointSelection
{
    private readonly Action<string> _failed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _output;
    private readonly Action<int> _settled;
    private readonly Func<bool> _stopped;
    private EndpointSelectionTracker _tracker;

    /// <summary>Creates the selection writer for one direction.</summary>
    /// <param name="output">True for playback, false for recording.</param>
    /// <param name="stopped">Whether the owner has been disposed; read on workers and the UI thread.</param>
    /// <param name="settled">Called on the UI thread with the write's HRESULT while it is still the newest choice.</param>
    /// <param name="failed">Called on a worker with the message of a newest write that threw.</param>
    internal AudioEndpointSelection(bool output, Func<bool> stopped, Action<int> settled, Action<string> failed)
    {
        _output = output;
        _stopped = stopped;
        _settled = settled;
        _failed = failed;
    }

    /// <summary>Gets the direction's name for log and failure text.</summary>
    internal string Kind => _output ? "output" : "input";

    /// <summary>
    ///     Gets whether a selection is still in flight, so the refresh path does not overwrite the
    ///     user's choice with a stale default meanwhile.
    /// </summary>
    internal bool Pending => _tracker.Pending;

    /// <summary>Makes every in-flight selection stale, so its completion cannot publish.</summary>
    internal void Invalidate()
    {
        _tracker.Begin();
    }

    /// <summary>Queues a default-endpoint write for the user's choice.</summary>
    /// <param name="endpoint">The chosen endpoint row.</param>
    internal void Select(AudioEndpointEntry endpoint)
    {
        var revision = _tracker.Begin();
        Log.Info($"Audio {Kind} selected: '{endpoint.Name}'.");
        var endpointId = endpoint.Id;
        _ = Task.Run(() => WriteAsync(endpointId, revision));
    }

    private async Task WriteAsync(string endpointId, int revision)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped() || !_tracker.IsCurrent(revision))
            {
                return;
            }

            try
            {
                var result = CoreAudio.SetDefaultEndpoint(endpointId);
                if (result >= 0 && _output)
                {
                    VolumeFeedback.Reinitialize();
                }

                _tracker.Complete(revision);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_stopped() || !_tracker.IsCurrent(revision))
                    {
                        return;
                    }

                    _settled(result);
                });
            }
            catch (Exception ex)
            {
                if (_tracker.IsCurrent(revision))
                {
                    _tracker.Complete(revision);
                    _failed($"Audio {Kind} selection failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
