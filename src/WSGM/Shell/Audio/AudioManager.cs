using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Live master-volume and default audio-device state for the quick access
///     sheet. Potentially slow Core Audio enumeration runs away from the Avalonia
///     UI thread.
/// </summary>
public sealed class AudioManager : ObservableObject, IDisposable
{
    /// <summary>How often the state is re-read without a notification.</summary>
    internal static readonly TimeSpan SafetyPollInterval = TimeSpan.FromSeconds(10);

    private readonly AudioChangeWatch _changes;
    private readonly AudioEndpointSelection _inputSelection;
    private readonly CoalescingVolumeWrite _inputVolumeWrite = new("Microphone volume");
    private readonly AudioEndpointSelection _outputSelection;
    private readonly CoalescingVolumeWrite _volumeWrite = new("Volume");
    private bool _disposed;
    private string _endpointSummary = "";
    private bool _hasOutputSnapshot;

    private bool _inputMuted;

    private double? _inputVolumePercent;
    private int _inputVolumeRevision;

    // A change notified while a refresh is running would be lost with the one-flight guard alone;
    // the pending flags make the running refresh queue one more.
    private bool _refreshPending;
    private bool _refreshPendingEndpoints;
    private int _refreshing;

    private AudioEndpointEntry? _selectedInput;

    private AudioEndpointEntry? _selectedOutput;
    private bool _stickyError;
    private int _ticks;

    // Core Audio pushes changes through _changes; the timer is only a safety net.
    private DispatcherTimer? _timer;

    private double _volumePercent;
    private int _volumeRevision;

    /// <summary>Creates an inert manager; <see cref="Start" /> begins reading and watching Core Audio.</summary>
    public AudioManager()
    {
        _changes = new AudioChangeWatch(() => QueueRefresh(false), () => QueueRefresh(true));
        _outputSelection = new AudioEndpointSelection(
            output: true,
            () => _disposed,
            result => OnSelectionSettled("output", result),
            message => PostFailure(message, sticky: true));
        _inputSelection = new AudioEndpointSelection(
            output: false,
            () => _disposed,
            result => OnSelectionSettled("input", result),
            message => PostFailure(message, sticky: true));
    }

    /// <summary>Gets the active playback endpoints.</summary>
    public ObservableCollection<AudioEndpointEntry> OutputEndpoints { get; } = [];

    /// <summary>Gets the active recording endpoints.</summary>
    public ObservableCollection<AudioEndpointEntry> InputEndpoints { get; } = [];

    /// <summary>
    ///     Gets or sets the default output's master volume, from 0 to 100.
    ///     Setting it also queues the shared audible preview.
    /// </summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var normalized = NormalizeVolume(value);
            if (Math.Abs(_volumePercent - normalized) < 0.01)
            {
                return;
            }

            _volumePercent = normalized;
            Interlocked.Increment(ref _volumeRevision);
            if (normalized > 0)
            {
                Muted = false;
            }

            Raise(nameof(VolumePercent));
            Raise(nameof(VolumeText));
            _volumeWrite.Queue(normalized, () => _disposed, WriteVolume, FailSticky);
        }
    }

    /// <summary>Gets the current master volume as display text.</summary>
    public string VolumeText => $"{(int)_volumePercent}%";

    /// <summary>Gets or sets the default input's master volume, from 0 to 100.</summary>
    /// <remarks>Null means Windows currently has no readable default capture endpoint.</remarks>
    public double? InputVolumePercent
    {
        get => _inputVolumePercent;
        set
        {
            if (value is not { } requested)
            {
                return;
            }

            var normalized = NormalizeVolume(requested);
            if (_inputVolumePercent is { } current && Math.Abs(current - normalized) < 0.01)
            {
                return;
            }

            _inputVolumePercent = normalized;
            Interlocked.Increment(ref _inputVolumeRevision);
            if (normalized > 0)
            {
                InputMuted = false;
            }

            Raise(nameof(InputVolumePercent));
            _inputVolumeWrite.Queue(normalized, () => _disposed, WriteInputVolume, FailSticky);
        }
    }

    /// <summary>Gets whether the default input endpoint is muted.</summary>
    public bool InputMuted
    {
        get => _inputMuted;
        private set
        {
            if (_inputMuted == value)
            {
                return;
            }

            _inputMuted = value;
            Raise(nameof(InputMuted));
        }
    }

    /// <summary>Gets whether the default output endpoint is muted.</summary>
    public bool Muted
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(Muted)))
            {
                return;
            }

            Raise(nameof(VolumeText));
        }
    }

    /// <summary>Gets or sets the default audio output.</summary>
    public AudioEndpointEntry? SelectedOutput
    {
        get => _selectedOutput;
        set => SelectEndpoint(value, true);
    }

    /// <summary>Gets or sets the default audio input.</summary>
    public AudioEndpointEntry? SelectedInput
    {
        get => _selectedInput;
        set => SelectEndpoint(value, false);
    }

    /// <summary>Gets a non-fatal audio error to show in the panel.</summary>
    public string ErrorText
    {
        get;
        private set
        {
            if (!SetFieldIfChanged(ref field, value, nameof(ErrorText)))
            {
                return;
            }

            Raise(nameof(HasError));
        }
    } = "";

    /// <summary>Gets whether <see cref="ErrorText" /> should be visible.</summary>
    public bool HasError => ErrorText.Length > 0;


    /// <summary>
    ///     Stops refreshes and prevents pending native work from publishing
    ///     into a closed sheet.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        // Invalidate every in-flight selection so its completion cannot publish.
        _outputSelection.Invalidate();
        _inputSelection.Invalidate();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        _volumeWrite.Clear();
        _inputVolumeWrite.Clear();
        _changes.Dispose();
    }

    /// <summary>
    ///     Performs an immediate refresh and starts live audio updates.
    ///     UI-thread callers only. Idempotent.
    /// </summary>
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        VolumeFeedback.Initialize();
        QueueRefresh(true);
        _changes.Start();
        // Core Audio reports every change; this only catches a notification that never arrived.
        // Polling once a second for the whole session was two COM round trips per second and an
        // endpoint enumeration every five, with the overlay closed and a game in the foreground.
        _timer = new DispatcherTimer { Interval = SafetyPollInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Requests a fresh volume and device enumeration.</summary>
    public void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }

        _stickyError = false;
        QueueRefresh(true);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        QueueRefresh(_ticks % 6 == 0);
    }

    private void QueueRefresh(bool includeEndpoints)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            _refreshPending = true;
            _refreshPendingEndpoints |= includeEndpoints;
            return;
        }

        var volumeRevision = Volatile.Read(ref _volumeRevision);
        var inputVolumeRevision = Volatile.Read(ref _inputVolumeRevision);
        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = AudioSnapshot.Read(includeEndpoints, volumeRevision, inputVolumeRevision);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                    {
                        Apply(snapshot);
                    }
                });
            }
            catch (Exception ex)
            {
                PostFailure($"Audio refresh failed: {ex.Message}", true);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
                Dispatcher.UIThread.Post(RunPendingRefresh);
            }
        });
    }

    private void RunPendingRefresh()
    {
        if (!_refreshPending)
        {
            return;
        }

        var includeEndpoints = _refreshPendingEndpoints;
        _refreshPending = false;
        _refreshPendingEndpoints = false;
        QueueRefresh(includeEndpoints);
    }

    private void Apply(AudioSnapshot snapshot)
    {
        if (snapshot.VolumeResult >= 0)
        {
            if (snapshot.VolumeRevision == Volatile.Read(ref _volumeRevision))
            {
                ApplyVolume(snapshot.Volume, snapshot.Muted);
                if (!_stickyError)
                {
                    ErrorText = "";
                }
            }
        }
        else
        {
            SetFailure("read volume", snapshot.VolumeResult);
        }

        if (snapshot.InputVolumeRevision == Volatile.Read(ref _inputVolumeRevision))
        {
            if (snapshot.InputVolumeResult >= 0)
            {
                ApplyInputVolume(snapshot.InputVolume, snapshot.InputMuted);
                Log.Change(
                    "audio.capture.volume",
                    $"Default microphone volume is {snapshot.InputVolume}% "
                    + $"(muted={snapshot.InputMuted}).");
            }
            else
            {
                ClearInputVolume();
                Log.Change(
                    "audio.capture.volume",
                    $"Default microphone volume is unavailable "
                    + $"(HRESULT 0x{snapshot.InputVolumeResult:X8}).");
            }
        }

        if (!snapshot.IncludedEndpoints)
        {
            return;
        }

        if (snapshot.OutputResult >= 0)
        {
            Reconcile(OutputEndpoints, snapshot.Outputs);
            if (!_outputSelection.Pending)
            {
                var previousOutputId = _selectedOutput?.Id;
                var defaultOutput = FindDefault(OutputEndpoints, snapshot.Outputs);
                SetSelected(true, defaultOutput);
                if (_hasOutputSnapshot
                    && !string.Equals(previousOutputId, defaultOutput?.Id, StringComparison.Ordinal))
                {
                    Log.Info("Default audio output changed outside WSGM; reopening the volume feedback stream.");
                    VolumeFeedback.Reinitialize();
                }

                _hasOutputSnapshot = true;
            }
        }
        else
        {
            SetFailure("list audio outputs", snapshot.OutputResult);
        }

        if (snapshot.InputResult >= 0)
        {
            Reconcile(InputEndpoints, snapshot.Inputs);
            if (!_inputSelection.Pending)
            {
                SetSelected(false, FindDefault(InputEndpoints, snapshot.Inputs));
            }
        }
        else
        {
            SetFailure("list audio inputs", snapshot.InputResult);
        }

        if (snapshot is not { OutputResult: >= 0, InputResult: >= 0 })
        {
            return;
        }

        var summary = $"Audio endpoints: {OutputEndpoints.Count} output(s), "
                      + $"default='{SelectedOutput?.Name ?? "none"}'; {InputEndpoints.Count} input(s), "
                      + $"default='{SelectedInput?.Name ?? "none"}'; volume={(int)VolumePercent}%, muted={Muted}; "
                      + $"microphone={(InputVolumePercent is { } inputVolume
                          ? inputVolume.ToString("0", CultureInfo.InvariantCulture) + "%"
                          : "unavailable")}, "
                      + $"muted={InputMuted}.";
        if (_endpointSummary == summary)
        {
            return;
        }

        _endpointSummary = summary;
        Log.Info(summary);
    }

    private void ApplyVolume(int percentage, bool muted)
    {
        var normalized = NormalizeVolume(percentage);
        if (Math.Abs(_volumePercent - normalized) >= 0.01)
        {
            _volumePercent = normalized;
            Raise(nameof(VolumePercent));
            Raise(nameof(VolumeText));
        }

        Muted = muted;
    }

    /// <summary>Records what Windows reports for the capture endpoint, without writing to it.</summary>
    /// <remarks>
    ///     Internal rather than private so the Steam projection can be tested against observed state.
    ///     The <see cref="InputVolumePercent" /> setter is the user's path and queues a hardware write;
    ///     a test driving that would change the volume on the machine running the suite.
    /// </remarks>
    /// <param name="percentage">Observed volume, 0 to 100.</param>
    /// <param name="muted">Observed mute state.</param>
    internal void ApplyInputVolume(int percentage, bool muted)
    {
        var normalized = NormalizeVolume(percentage);
        if (_inputVolumePercent is not { } current || Math.Abs(current - normalized) >= 0.01)
        {
            _inputVolumePercent = normalized;
            Raise(nameof(InputVolumePercent));
        }

        InputMuted = muted;
    }

    private void ClearInputVolume()
    {
        if (_inputVolumePercent is null)
        {
            return;
        }

        _inputVolumePercent = null;
        _inputMuted = false;
        Raise(nameof(InputVolumePercent));
        Raise(nameof(InputMuted));
    }

    private static AudioEndpointEntry? FindDefault(
        ObservableCollection<AudioEndpointEntry> entries,
        IReadOnlyList<CoreAudio.AudioEndpoint> snapshot)
    {
        var defaultId = snapshot
            .Where(endpoint => endpoint.IsDefault)
            .Select(string? (endpoint) => endpoint.Id)
            .FirstOrDefault();
        return defaultId is null
            ? null
            : entries.FirstOrDefault(entry => entry.Id == defaultId);
    }

    /// <summary>
    ///     Reconciles endpoint rows in place so a periodic refresh does not
    ///     destroy an open combo box or its focused item.
    /// </summary>
    /// <param name="entries">UI-owned rows to update in place; retained endpoints keep their row instances and order.</param>
    /// <param name="fresh">Current endpoints; missing rows are removed and new IDs appended in snapshot order.</param>
    internal static void Reconcile(
        ObservableCollection<AudioEndpointEntry> entries,
        IReadOnlyList<CoreAudio.AudioEndpoint> fresh)
    {
        var remaining = new Dictionary<string, CoreAudio.AudioEndpoint>(StringComparer.Ordinal);
        foreach (var endpoint in fresh)
        {
            remaining.TryAdd(endpoint.Id, endpoint);
        }

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (remaining.Remove(entry.Id, out var endpoint))
            {
                entry.Name = AudioEndpointText.Name(endpoint);
            }
            else
            {
                entries.RemoveAt(index);
            }
        }

        foreach (var endpoint in fresh)
        {
            if (remaining.Remove(endpoint.Id))
            {
                entries.Add(new AudioEndpointEntry(endpoint.Id, AudioEndpointText.Name(endpoint)));
            }
        }
    }

    private void SelectEndpoint(AudioEndpointEntry? value, bool output)
    {
        var current = output ? _selectedOutput : _selectedInput;
        if (value is null || ReferenceEquals(current, value) || current?.Id == value.Id)
        {
            return;
        }

        SetSelected(output, value);
        (output ? _outputSelection : _inputSelection).Select(value);
    }

    /// <summary>Publishes the newest selection's outcome. UI thread, while still current.</summary>
    /// <param name="kind">The direction whose write settled, for the failure text.</param>
    /// <param name="result">The write's HRESULT.</param>
    private void OnSelectionSettled(string kind, int result)
    {
        if (result < 0)
        {
            PostFailure(
                $"Could not select audio {kind} (HRESULT 0x{result:X8}).",
                sticky: true);
        }
        else
        {
            _stickyError = false;
            ErrorText = "";
        }

        QueueRefresh(true);
    }

    private void SetSelected(bool output, AudioEndpointEntry? value)
    {
        if (output)
        {
            if (ReferenceEquals(_selectedOutput, value))
            {
                return;
            }

            _selectedOutput = value;
            Raise(nameof(SelectedOutput));
        }
        else if (!ReferenceEquals(_selectedInput, value))
        {
            _selectedInput = value;
            Raise(nameof(SelectedInput));
        }
    }

    private void WriteVolume(int requested)
    {
        var result = CoreAudio.SetVolume(requested, out var muted);
        if (result < 0)
        {
            PostFailure($"Set volume failed (HRESULT 0x{result:X8}).", true);
            return;
        }

        Log.Info($"Audio panel volume set to {requested}% (muted={muted != 0}).");
        VolumeFeedback.Play();
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            ApplyVolume(requested, muted != 0);
            _stickyError = false;
            ErrorText = "";
        });
    }

    private void WriteInputVolume(int requested)
    {
        var result = CoreAudio.SetVolume(CoreAudio.AudioDirection.Capture, requested, out var muted);
        if (result < 0)
        {
            PostFailure($"Set microphone volume failed (HRESULT 0x{result:X8}).", true);
            return;
        }

        Log.Info($"Microphone volume set to {requested}% (muted={muted != 0}).");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            ApplyInputVolume(requested, muted != 0);
            _stickyError = false;
            ErrorText = "";
        });
    }

    private void FailSticky(string message)
    {
        PostFailure(message, true);
    }

    /// <summary>
    ///     Adopts a volume state another WSGM writer has ALREADY applied to
    ///     Core Audio (the hardware volume buttons), so the audio panel slider does not
    ///     lag one poll behind the OSD. Presentation only: nothing is written back,
    ///     and the revision bump keeps an older in-flight snapshot from undoing the
    ///     adopted value before the next poll confirms it.
    /// </summary>
    /// <param name="percent">The volume the writer landed on, 0-100.</param>
    /// <param name="muted">The mute state the writer landed on.</param>
    /// <remarks>Callable from any thread; the slider update is posted to the UI thread.</remarks>
    internal void NoteExternalVolume(int percent, bool muted)
    {
        Interlocked.Increment(ref _volumeRevision);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ApplyVolume(percent, muted);
            }
        });
    }

    /// <summary>Rounds and bounds a slider value for Core Audio.</summary>
    /// <param name="value">The raw slider or endpoint value.</param>
    /// <returns>An integer from 0 through 100.</returns>
    internal static int NormalizeVolume(double value)
    {
        return double.IsFinite(value) ? Math.Clamp((int)Math.Round(value), 0, 100) : 0;
    }

    private void SetFailure(string operation, int result)
    {
        PostFailure($"Could not {operation} (HRESULT 0x{result:X8}).");
    }

    private void PostFailure(string message, bool sticky = false)
    {
        Log.Warn(message);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            _stickyError |= sticky;
            ErrorText = message;
        });
    }
}
