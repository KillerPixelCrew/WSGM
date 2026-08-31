using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One active Core Audio endpoint shown by the taskbar audio panel.</summary>
public sealed class AudioEndpointEntry : INotifyPropertyChanged
{
    /// <summary>Raised when the endpoint's presentation changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Creates a visible endpoint row.</summary>
    /// <param name="id">The opaque Windows endpoint identifier.</param>
    /// <param name="name">The friendly device name.</param>
    internal AudioEndpointEntry(string id, string name)
    {
        Id = id;
        _name = name;
    }

    /// <summary>Gets the opaque Windows endpoint identifier.</summary>
    internal string Id { get; }

    private string _name;

    /// <summary>Gets the friendly device name.</summary>
    public string Name
    {
        get => _name;
        internal set
        {
            if (_name != value)
            {
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }
    }
}

/// <summary>Live master-volume and default audio-device state for the game-mode
/// taskbar. Potentially slow Core Audio enumeration runs away from the Avalonia
/// UI thread.</summary>
public sealed class AudioManager : INotifyPropertyChanged, IDisposable
{
    private readonly Func<string, int> _setDefaultEndpoint;
    private readonly Action<Action> _postEndpointCompletion;

    /// <summary>Creates a manager backed by managed Core Audio interop.</summary>
    public AudioManager()
        : this(CoreAudio.SetDefaultEndpoint, action => Dispatcher.UIThread.Post(action))
    {
    }

    /// <summary>Creates a manager with endpoint-selection seams for isolated tests.</summary>
    internal AudioManager(Func<string, int> setDefaultEndpoint, Action<Action> postEndpointCompletion)
    {
        _setDefaultEndpoint = setDefaultEndpoint;
        _postEndpointCompletion = postEndpointCompletion;
    }

    /// <summary>Raised after a bindable audio property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the active playback endpoints.</summary>
    public ObservableCollection<AudioEndpointEntry> OutputEndpoints { get; } = [];

    /// <summary>Gets the active recording endpoints.</summary>
    public ObservableCollection<AudioEndpointEntry> InputEndpoints { get; } = [];

    private double _volumePercent;

    /// <summary>Gets or sets the default output's master volume, from 0 to 100.
    /// Setting it also queues the shared audible preview.</summary>
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
            QueueVolumeWrite(normalized);
        }
    }

    /// <summary>Gets the current master volume as display text.</summary>
    public string VolumeText => $"{(int)_volumePercent}%";

    private bool _muted;

    /// <summary>Gets whether the default output endpoint is muted.</summary>
    public bool Muted
    {
        get => _muted;
        private set
        {
            if (_muted != value)
            {
                _muted = value;
                Raise(nameof(Muted));
                Raise(nameof(VolumeText));
            }
        }
    }

    private AudioEndpointEntry? _selectedOutput;

    /// <summary>Gets or sets the default audio output.</summary>
    public AudioEndpointEntry? SelectedOutput
    {
        get => _selectedOutput;
        set => SelectEndpoint(value, output: true);
    }

    private AudioEndpointEntry? _selectedInput;

    /// <summary>Gets or sets the default audio input.</summary>
    public AudioEndpointEntry? SelectedInput
    {
        get => _selectedInput;
        set => SelectEndpoint(value, output: false);
    }

    private string _errorText = "";

    /// <summary>Gets a non-fatal audio error to show in the panel.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (_errorText != value)
            {
                _errorText = value;
                Raise(nameof(ErrorText));
                Raise(nameof(HasError));
            }
        }
    }

    /// <summary>Gets whether <see cref="ErrorText"/> should be visible.</summary>
    public bool HasError => ErrorText.Length > 0;

    private DispatcherTimer? _timer;
    private int _ticks;
    private int _refreshing;
    private int _volumeRevision;
    private bool _disposed;
    private bool _stickyError;
    private string _endpointSummary = "";
    private readonly object _volumeGate = new();
    private readonly SemaphoreSlim _outputSelectionGate = new(1, 1);
    private readonly SemaphoreSlim _inputSelectionGate = new(1, 1);
    private int? _pendingVolume;
    private bool _volumeWorkerRunning;
    private int _outputSelectionRevision;
    private int _inputSelectionRevision;
    private int _outputCompletedSelectionRevision;
    private int _inputCompletedSelectionRevision;
    private bool _hasOutputSnapshot;

    /// <summary>Performs an immediate refresh and starts live audio updates.
    /// UI-thread callers only. Idempotent.</summary>
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }
        VolumeFeedback.Initialize();
        QueueRefresh(includeEndpoints: true);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Requests a fresh volume and device enumeration.</summary>
    public void Refresh()
    {
        _stickyError = false;
        QueueRefresh(includeEndpoints: true);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        QueueRefresh(includeEndpoints: _ticks % 5 == 0);
    }

    private void QueueRefresh(bool includeEndpoints)
    {
        if (_disposed || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        var volumeRevision = Volatile.Read(ref _volumeRevision);
        _ = Task.Run(() =>
        {
            try
            {
                var snapshot = ReadSnapshot(includeEndpoints, volumeRevision);
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
                PostFailure($"Audio refresh failed: {ex.Message}", sticky: true);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private sealed record Snapshot(
        int VolumeResult,
        int Volume,
        bool Muted,
        int VolumeRevision,
        bool IncludedEndpoints,
        int OutputResult,
        IReadOnlyList<CoreAudio.AudioEndpoint> Outputs,
        int InputResult,
        IReadOnlyList<CoreAudio.AudioEndpoint> Inputs);

    private static Snapshot ReadSnapshot(bool includeEndpoints, int volumeRevision)
    {
        var volumeResult = CoreAudio.GetVolume(out var volume, out var muted);
        if (!includeEndpoints)
        {
            return new Snapshot(
                volumeResult,
                volume,
                muted != 0,
                volumeRevision,
                false,
                0,
                [],
                0,
                []);
        }

        var outputResult = CoreAudio.ListEndpoints(CoreAudio.Render, out var outputs);
        var inputResult = CoreAudio.ListEndpoints(CoreAudio.Capture, out var inputs);
        return new Snapshot(
            volumeResult,
            volume,
            muted != 0,
            volumeRevision,
            true,
            outputResult,
            outputs,
            inputResult,
            inputs);
    }

    private void Apply(Snapshot snapshot)
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

        if (!snapshot.IncludedEndpoints)
        {
            return;
        }
        if (snapshot.OutputResult >= 0)
        {
            Reconcile(OutputEndpoints, snapshot.Outputs);
            if (!EndpointSelectionPending(output: true))
            {
                var previousOutputId = _selectedOutput?.Id;
                var defaultOutput = FindDefault(OutputEndpoints, snapshot.Outputs);
                SetSelected(output: true, defaultOutput);
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
            if (!EndpointSelectionPending(output: false))
            {
                SetSelected(output: false, FindDefault(InputEndpoints, snapshot.Inputs));
            }
        }
        else
        {
            SetFailure("list audio inputs", snapshot.InputResult);
        }
        if (snapshot.OutputResult >= 0 && snapshot.InputResult >= 0)
        {
            var summary = $"Audio endpoints: {OutputEndpoints.Count} output(s), "
                + $"default='{SelectedOutput?.Name ?? "none"}'; {InputEndpoints.Count} input(s), "
                + $"default='{SelectedInput?.Name ?? "none"}'; volume={(int)VolumePercent}%, muted={Muted}.";
            if (_endpointSummary != summary)
            {
                _endpointSummary = summary;
                Log.Info(summary);
            }
        }
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

    private static AudioEndpointEntry? FindDefault(
        ObservableCollection<AudioEndpointEntry> entries,
        IReadOnlyList<CoreAudio.AudioEndpoint> snapshot)
    {
        string? defaultId = null;
        foreach (var endpoint in snapshot)
        {
            if (endpoint.IsDefault)
            {
                defaultId = endpoint.Id;
                break;
            }
        }
        if (defaultId is null)
        {
            return null;
        }
        foreach (var entry in entries)
        {
            if (entry.Id == defaultId)
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>Reconciles endpoint rows in place so a periodic refresh does not
    /// destroy an open combo box or its focused item.</summary>
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
                entry.Name = endpoint.Name;
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
                entries.Add(new AudioEndpointEntry(endpoint.Id, endpoint.Name));
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
        var kind = output ? "output" : "input";
        var revision = output
            ? Interlocked.Increment(ref _outputSelectionRevision)
            : Interlocked.Increment(ref _inputSelectionRevision);
        Log.Info($"Audio {kind} selected: '{value.Name}'.");
        _ = Task.Run(() => ApplyEndpointSelection(value.Id, output, kind, revision));
    }

    /// <summary>Serializes default-device writes for one data flow. A stale
    /// queued request is skipped, and an already-running stale request cannot
    /// publish UI state after the user's newer choice.</summary>
    private void ApplyEndpointSelection(string endpointId, bool output, string kind, int revision)
    {
        var gate = output ? _outputSelectionGate : _inputSelectionGate;
        gate.Wait();
        try
        {
            if (_disposed || !IsCurrentEndpointSelection(output, revision))
            {
                return;
            }
            try
            {
                var result = _setDefaultEndpoint(endpointId);
                if (result >= 0 && output)
                {
                    VolumeFeedback.Reinitialize();
                }
                if (IsCurrentEndpointSelection(output, revision))
                {
                    MarkEndpointSelectionCompleted(output, revision);
                }
                _postEndpointCompletion(() =>
                {
                    if (_disposed || !IsCurrentEndpointSelection(output, revision))
                    {
                        return;
                    }
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
                    QueueRefresh(includeEndpoints: true);
                });
            }
            catch (Exception ex)
            {
                if (IsCurrentEndpointSelection(output, revision))
                {
                    MarkEndpointSelectionCompleted(output, revision);
                    PostFailure($"Audio {kind} selection failed: {ex.Message}", sticky: true);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsCurrentEndpointSelection(bool output, int revision)
        => revision == (output
            ? Volatile.Read(ref _outputSelectionRevision)
            : Volatile.Read(ref _inputSelectionRevision));

    private bool EndpointSelectionPending(bool output)
        => (output
            ? Volatile.Read(ref _outputSelectionRevision)
            : Volatile.Read(ref _inputSelectionRevision))
            != (output
                ? Volatile.Read(ref _outputCompletedSelectionRevision)
                : Volatile.Read(ref _inputCompletedSelectionRevision));

    private void MarkEndpointSelectionCompleted(bool output, int revision)
    {
        if (output)
        {
            Volatile.Write(ref _outputCompletedSelectionRevision, revision);
        }
        else
        {
            Volatile.Write(ref _inputCompletedSelectionRevision, revision);
        }
    }

    private void SetSelected(bool output, AudioEndpointEntry? value)
    {
        if (output)
        {
            if (!ReferenceEquals(_selectedOutput, value))
            {
                _selectedOutput = value;
                Raise(nameof(SelectedOutput));
            }
        }
        else if (!ReferenceEquals(_selectedInput, value))
        {
            _selectedInput = value;
            Raise(nameof(SelectedInput));
        }
    }

    private void QueueVolumeWrite(int percentage)
    {
        lock (_volumeGate)
        {
            _pendingVolume = percentage;
            if (_volumeWorkerRunning)
            {
                return;
            }
            _volumeWorkerRunning = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int requested;
                    lock (_volumeGate)
                    {
                        if (_pendingVolume is not int pending || _disposed)
                        {
                            _volumeWorkerRunning = false;
                            return;
                        }
                        requested = pending;
                        _pendingVolume = null;
                    }

                    try
                    {
                        var result = CoreAudio.SetVolume(requested, out var muted);
                        if (result >= 0)
                        {
                            Log.Info($"Taskbar volume set to {requested}% (muted={muted != 0}).");
                            VolumeFeedback.Play();
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!_disposed)
                                {
                                    ApplyVolume(requested, muted != 0);
                                    _stickyError = false;
                                    ErrorText = "";
                                }
                            });
                        }
                        else
                        {
                            PostFailure($"Set volume failed (HRESULT 0x{result:X8}).", sticky: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A failed write is reported and the worker carries on.
                        // Letting it unwind the loop would
                        // strand _volumeWorkerRunning at true, and every later
                        // slider move would then be dropped in silence.
                        PostFailure($"Volume write failed: {ex.Message}", sticky: true);
                    }
                }
            }
            catch (Exception ex)
            {
                // The flag is the only thing that lets a later write start a
                // worker at all, so it must be cleared on the abnormal exit too.
                lock (_volumeGate)
                {
                    _volumeWorkerRunning = false;
                }
                Log.Warn($"Volume write worker stopped: {ex.Message}");
            }
        });
    }

    /// <summary>Rounds and bounds a slider value for Core Audio.</summary>
    /// <param name="value">The raw slider or endpoint value.</param>
    /// <returns>An integer from 0 through 100.</returns>
    internal static int NormalizeVolume(double value)
        => double.IsFinite(value) ? Math.Clamp((int)Math.Round(value), 0, 100) : 0;

    private void SetFailure(string operation, int result)
        => PostFailure($"Could not {operation} (HRESULT 0x{result:X8}).");

    private void PostFailure(string message, bool sticky = false)
    {
        Log.Warn(message);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _stickyError |= sticky;
                ErrorText = message;
            }
        });
    }

    private void Raise(string property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Stops refreshes and prevents pending native work from publishing
    /// into a closed taskbar.</summary>
    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _outputSelectionRevision);
        Interlocked.Increment(ref _inputSelectionRevision);
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
        lock (_volumeGate)
        {
            _pendingVolume = null;
        }
    }
}
