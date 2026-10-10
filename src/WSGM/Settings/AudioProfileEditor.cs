using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>Editable saved audio preferences for one display-switch direction.</summary>
public sealed class AudioProfileEditor : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<Action> _postToUi;
    private readonly Func<string?, AudioDiscovery> _read;
    private bool _applyMute;

    private bool _applyVolume;
    private bool _closed;

    // The last observation the editor drew from, and the read that produced it. Core Audio can
    // block on a wedged driver, so nothing here reads it on the dispatcher: the editor opens on
    // whatever the profile saved and fills in when a worker comes back.
    private AudioDiscovery _discovered = AudioDiscovery.Empty;
    private int _discoveryGeneration;
    private AudioFormatOption? _format;
    private AudioEndpointOption? _input;
    private bool _loading;
    private bool _muted;

    private AudioEndpointOption? _output;

    // What the profile holds, independent of what the machine can currently offer. A saved format
    // or spatial value the selected endpoint does not report right now has no option to select, and
    // clearing it here would delete a valid preference on the next unrelated save.
    private AudioFormatPreference? _savedFormat;
    private Guid? _savedSpatial;
    private SpatialAudioOption? _spatial;
    private int _volumePercent = 50;

    internal AudioProfileEditor(Action changed, Func<string?, AudioDiscovery> read, Action<Action> postToUi)
    {
        _changed = changed;
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
    }

    /// <summary>Playback endpoint choices, with a null entry meaning leave unchanged.</summary>
    public ObservableCollection<AudioEndpointOption> OutputChoices { get; } = [];

    /// <summary>Recording endpoint choices, with a null entry meaning leave unchanged.</summary>
    public ObservableCollection<AudioEndpointOption> InputChoices { get; } = [];

    /// <summary>Formats the selected playback endpoint currently reports as supported.</summary>
    public ObservableCollection<AudioFormatOption> FormatChoices { get; } = [];

    /// <summary>Spatial formats the selected playback endpoint currently reports as supported.</summary>
    public ObservableCollection<SpatialAudioOption> SpatialChoices { get; } = [];

    /// <summary>Selected playback endpoint, or null to leave the default unchanged.</summary>
    public AudioEndpointOption? Output
    {
        get => _output;
        set
        {
            if (_loading || !SetFieldIfChanged(ref _output, value, nameof(Output)))
            {
                return;
            }

            RefreshPlaybackCapabilities(false);
            // The new endpoint's capabilities are not in the last observation, so ask for them.
            Log.Observe(RefreshEndpointsAsync(), "Settings audio discovery");
            Edited();
        }
    }

    /// <summary>Selected recording endpoint, or null to leave the default unchanged.</summary>
    public AudioEndpointOption? Input
    {
        get => _input;
        set
        {
            if (!_loading && SetFieldIfChanged(ref _input, value, nameof(Input)))
            {
                Edited();
            }
        }
    }

    /// <summary>Whether this profile applies a playback volume.</summary>
    public bool ApplyVolume
    {
        get => _applyVolume;
        set
        {
            if (SetFieldIfChanged(ref _applyVolume, value, nameof(ApplyVolume)))
            {
                Edited();
            }
        }
    }

    /// <summary>Saved playback volume, used only when <see cref="ApplyVolume" /> is true.</summary>
    public int VolumePercent
    {
        get => _volumePercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (SetFieldIfChanged(ref _volumePercent, normalized, nameof(VolumePercent)))
            {
                Edited();
            }
        }
    }

    /// <summary>Whether this profile applies a playback mute value.</summary>
    public bool ApplyMute
    {
        get => _applyMute;
        set
        {
            if (SetFieldIfChanged(ref _applyMute, value, nameof(ApplyMute)))
            {
                Edited();
            }
        }
    }

    /// <summary>Saved playback mute state, used only when <see cref="ApplyMute" /> is true.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetFieldIfChanged(ref _muted, value, nameof(Muted)))
            {
                Edited();
            }
        }
    }

    /// <summary>Selected playback default format, or null to leave it unchanged.</summary>
    public AudioFormatOption? PlaybackFormat
    {
        get => _format;
        set
        {
            if (_loading || !SetFieldIfChanged(ref _format, value, nameof(PlaybackFormat)))
            {
                return;
            }

            _savedFormat = value is null ? null : AudioProfileService.ToPreference(value.Format);
            Edited();
        }
    }

    /// <summary>Selected spatial format, or null to leave it unchanged.</summary>
    public SpatialAudioOption? SpatialFormat
    {
        get => _spatial;
        set
        {
            if (_loading || !SetFieldIfChanged(ref _spatial, value, nameof(SpatialFormat)))
            {
                return;
            }

            _savedSpatial = value?.Format;
            Edited();
        }
    }

    /// <summary>
    ///     Re-reads the live endpoints, and the selected output's capabilities, on a worker and
    ///     publishes the result. Writes no Windows audio state.
    /// </summary>
    /// <returns>Completion of the read and of the publication that follows it.</returns>
    public async Task RefreshEndpointsAsync()
    {
        if (_closed)
        {
            return;
        }

        var generation = ++_discoveryGeneration;
        var endpointId = _output?.Id;
        var discovered = await Task.Run(() => _read(endpointId)).ConfigureAwait(false);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _postToUi(() =>
        {
            // Collection changes and two-way selections belong to the dispatcher, even when
            // the caller has no synchronization context. A stale endpoint read cannot replace
            // a newer selection or a profile loaded while that read was in flight.
            try
            {
                if (!_closed && generation == _discoveryGeneration)
                {
                    Publish(discovered);
                }

                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Ends publication when the owning Settings window closes; a pending native read is not repeated.</summary>
    internal void StopDiscovery()
    {
        _closed = true;
        ++_discoveryGeneration;
    }

    private void Publish(AudioDiscovery discovered)
    {
        _discovered = discovered;
        var preferredOutput = _output?.Id;
        var preferredInput = _input?.Id;
        _loading = true;
        try
        {
            OutputChoices.Clear();
            InputChoices.Clear();
            foreach (var option in discovered.Outputs)
            {
                OutputChoices.Add(option);
            }

            foreach (var option in discovered.Inputs)
            {
                InputChoices.Add(option);
            }

            // Clearing a bound ItemsSource sends SelectedItem=null back through Avalonia's
            // two-way binding. Ignore that publication feedback for the entire rebuild.
            _output = Keep(OutputChoices, preferredOutput, _output);
            _input = Keep(InputChoices, preferredInput, _input);
            RefreshPlaybackCapabilities(true);
            Raise(nameof(Output));
            Raise(nameof(Input));
        }
        finally
        {
            _loading = false;
        }
    }

    private static AudioEndpointOption? Keep(
        ObservableCollection<AudioEndpointOption> choices,
        string? preferredId,
        AudioEndpointOption? previous)
    {
        if (preferredId is null)
        {
            return null;
        }

        if (choices.FirstOrDefault(choice => choice.Id == preferredId) is { } live)
        {
            return live;
        }

        if (previous is null)
        {
            return null;
        }

        choices.Add(previous);
        return previous;
    }

    /// <summary>Seeds this draft from persisted settings without changing Windows audio.</summary>
    /// <param name="preference">Saved draft to load; null clears optional choices without changing live audio.</param>
    internal void Load(AudioProfilePreference? preference)
    {
        ++_discoveryGeneration;
        _loading = true;
        var output = preference?.Output;
        var input = preference?.Input;
        if (output?.Id is { } outputId && OutputChoices.All(choice => choice.Id != outputId))
        {
            OutputChoices.Add(new AudioEndpointOption(outputId, output.Name ?? "Unavailable playback device"));
        }

        if (input?.Id is { } inputId && InputChoices.All(choice => choice.Id != inputId))
        {
            InputChoices.Add(new AudioEndpointOption(inputId, input.Name ?? "Unavailable recording device"));
        }

        _output = OutputChoices.FirstOrDefault(choice => choice.Id == output?.Id);
        _input = InputChoices.FirstOrDefault(choice => choice.Id == input?.Id);
        _applyVolume = preference?.VolumePercent is not null;
        _volumePercent = preference?.VolumePercent ?? 50;
        _applyMute = preference?.Muted is not null;
        _muted = preference?.Muted ?? false;
        _savedFormat = preference?.PlaybackFormat;
        _savedSpatial = preference?.SpatialFormat;
        RefreshPlaybackCapabilities(true);
        _loading = false;
        Raise(nameof(Output));
        Raise(nameof(Input));
        Raise(nameof(ApplyVolume));
        Raise(nameof(VolumePercent));
        Raise(nameof(ApplyMute));
        Raise(nameof(Muted));
        Raise(nameof(PlaybackFormat));
        Raise(nameof(SpatialFormat));
    }

    /// <summary>Builds the nullable persisted preference from this draft.</summary>
    /// <returns>
    ///     A detached preference preserving unavailable saved endpoint/format choices, or null when nothing is
    ///     configured.
    /// </returns>
    internal AudioProfilePreference? Build()
    {
        var result = new AudioProfilePreference
        {
            Output = _output is null ? null : new AudioEndpointPreference { Id = _output.Id, Name = _output.Name },
            Input = _input is null ? null : new AudioEndpointPreference { Id = _input.Id, Name = _input.Name },
            VolumePercent = _applyVolume ? _volumePercent : null,
            Muted = _applyMute ? _muted : null,
            // The selection when the endpoint reports it, the saved value when it does not.
            PlaybackFormat = _format is null ? _savedFormat : AudioProfileService.ToPreference(_format.Format),
            SpatialFormat = _spatial?.Format ?? _savedSpatial
        };
        return result.Output is not null
               || result.Input is not null
               || result.VolumePercent is not null
               || result.Muted is not null
               || result.PlaybackFormat is not null
               || result.SpatialFormat is not null
            ? result
            : null;
    }

    /// <summary>Rebuilds the capability choices for the selected endpoint.</summary>
    /// <param name="keepSaved">
    ///     Whether the saved format and spatial values survive. They do for a reload, where nothing
    ///     the user did caused the rebuild, and they do not when the user picks another endpoint: a
    ///     format belongs to the endpoint that reported it.
    /// </param>
    private void RefreshPlaybackCapabilities(bool keepSaved)
    {
        if (!keepSaved)
        {
            _savedFormat = null;
            _savedSpatial = null;
        }

        var loading = _loading;
        _loading = true;
        try
        {
            FormatChoices.Clear();
            SpatialChoices.Clear();
            _format = null;
            _spatial = null;
            // Only the endpoint the last observation actually read has capabilities to offer. Another
            // one has none yet, which is a pending read rather than an endpoint without choices.
            if (_discovered.EndpointId is { } endpointId && (_output is null || _output.Id == endpointId))
            {
                foreach (var format in _discovered.Formats ?? [])
                {
                    FormatChoices.Add(new AudioFormatOption(format));
                }

                if (_discovered.SpatialFormats is { } spatial)
                {
                    SpatialChoices.Add(new SpatialAudioOption(CoreAudio.SpatialAudioFormats.Off, "Off"));
                    foreach (var format in spatial)
                    {
                        SpatialChoices.Add(new SpatialAudioOption(format, SpatialAudioNames.For(format)));
                    }
                }

                _format = FormatChoices.FirstOrDefault(choice => Same(choice.Format, _savedFormat));
                _spatial = _savedSpatial is { } savedSpatial
                    ? SpatialChoices.FirstOrDefault(choice => choice.Format == savedSpatial)
                    : null;
            }

            Raise(nameof(PlaybackFormat));
            Raise(nameof(SpatialFormat));
        }
        finally
        {
            _loading = loading;
        }
    }

    private void Edited()
    {
        if (!_loading)
        {
            _changed();
        }
    }

    private static bool Same(CoreAudio.AudioDeviceFormat format, AudioFormatPreference? preference)
    {
        return preference is not null
               && format.Channels == preference.Channels
               && format.SampleRate == preference.SampleRate
               && format.BitsPerSample == preference.BitsPerSample
               && format.ContainerBitsPerSample == preference.ContainerBitsPerSample
               && format.ChannelMask == preference.ChannelMask
               && format.IsFloat == preference.IsFloat;
    }
}

/// <summary>One named default-format choice.</summary>
/// <param name="Format">
///     Native playback format retained for persistence and shown with its sample-rate/bit-depth/channel
///     label.
/// </param>
public sealed record AudioFormatOption(CoreAudio.AudioDeviceFormat Format)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Format.Channels} channels · {Format.SampleRate / 1000.0:0.#} kHz · {Format.BitsPerSample}-bit";
    }
}
