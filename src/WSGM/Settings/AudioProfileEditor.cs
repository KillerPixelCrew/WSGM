using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>
///     One reading of what the machine currently offers: the endpoints, and the capabilities of the
///     one endpoint that was asked about.
/// </summary>
/// <param name="Outputs">Render endpoints, in enumeration order.</param>
/// <param name="Inputs">Capture endpoints, in enumeration order.</param>
/// <param name="EndpointId">The endpoint the capabilities below describe, or null when none was read.</param>
/// <param name="Formats">The device formats that endpoint reports, or null when the read failed.</param>
/// <param name="SpatialFormats">The spatial formats that endpoint reports, or null when the read failed.</param>
internal sealed record AudioDiscovery(
    IReadOnlyList<AudioEndpointOption> Outputs,
    IReadOnlyList<AudioEndpointOption> Inputs,
    string? EndpointId,
    IReadOnlyList<CoreAudio.AudioDeviceFormat>? Formats,
    IReadOnlyList<Guid>? SpatialFormats)
{
    /// <summary>Nothing observed, which is what a machine without Core Audio offers.</summary>
    internal static AudioDiscovery Empty { get; } = new([], [], null, null, null);

    /// <summary>Reads Windows' endpoints, and one endpoint's capabilities when one is named.</summary>
    /// <param name="endpointId">The endpoint whose capabilities to read, or null for none.</param>
    /// <returns>The observation. Every call here is a blocking Core Audio read.</returns>
    internal static AudioDiscovery Read(string? endpointId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Empty;
        }

        var outputs = Endpoints(CoreAudio.AudioDirection.Render);
        var inputs = Endpoints(CoreAudio.AudioDirection.Capture);
        if (endpointId is null)
        {
            return new AudioDiscovery(outputs, inputs, null, null, null);
        }

        var formats = CoreAudio.ListSupportedDeviceFormats(endpointId, out var supported) >= 0
            ? supported
            : null;
        var spatial = CoreAudio.GetSpatialAudio(endpointId, out var state) >= 0
            ? state.SupportedFormats
            : null;
        return new AudioDiscovery(outputs, inputs, endpointId, formats, spatial);
    }

    private static IReadOnlyList<AudioEndpointOption> Endpoints(CoreAudio.AudioDirection direction)
    {
        return CoreAudio.ListEndpoints(direction, out var endpoints) >= 0
            ? [.. endpoints.Select(static endpoint => new AudioEndpointOption(endpoint.Id, endpoint.Name))]
            : [];
    }
}

/// <summary>Editable saved audio preferences for one display-switch direction.</summary>
public sealed class AudioProfileEditor : ObservableObject
{
    private readonly Action _changed;
    private readonly Func<string?, AudioDiscovery> _read;
    private bool _applyMute;

    private bool _applyVolume;

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

    internal AudioProfileEditor(Action changed, Func<string?, AudioDiscovery>? read = null)
    {
        _changed = changed;
        _read = read ?? AudioDiscovery.Read;
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
            if (!SetFieldIfChanged(ref _output, value, nameof(Output)))
            {
                return;
            }

            RefreshPlaybackCapabilities(false);
            // The new endpoint's capabilities are not in the last observation, so ask for them.
            _ = RefreshEndpointsAsync();
            Edited();
        }
    }

    /// <summary>Selected recording endpoint, or null to leave the default unchanged.</summary>
    public AudioEndpointOption? Input
    {
        get => _input;
        set
        {
            if (SetFieldIfChanged(ref _input, value, nameof(Input)))
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
            if (!SetFieldIfChanged(ref _format, value, nameof(PlaybackFormat)))
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
            if (!SetFieldIfChanged(ref _spatial, value, nameof(SpatialFormat)))
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
        var generation = ++_discoveryGeneration;
        var endpointId = _output?.Id;
        var discovered = await Task.Run(() => _read(endpointId)).ConfigureAwait(true);
        // A newer read is already on its way, and it was started against a newer selection.
        if (generation == _discoveryGeneration)
        {
            Publish(discovered);
        }
    }

    private void Publish(AudioDiscovery discovered)
    {
        _discovered = discovered;
        var preferredOutput = _output?.Id;
        var preferredInput = _input?.Id;
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

        _loading = true;
        // A saved endpoint that is not enumerated right now keeps its entry and stays selected. It
        // is a preference for a device that is off or unplugged, not a value to discard.
        _output = Keep(OutputChoices, preferredOutput, _output);
        _input = Keep(InputChoices, preferredInput, _input);
        _loading = false;
        RefreshPlaybackCapabilities(true);
        Raise(nameof(Output));
        Raise(nameof(Input));
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
    internal void Load(AudioProfilePreference? preference)
    {
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

        FormatChoices.Clear();
        SpatialChoices.Clear();
        _format = null;
        _spatial = null;
        // Only the endpoint the last observation actually read has capabilities to offer. Another
        // one has none yet, which is a pending read rather than an endpoint without choices.
        if (_output?.Id is { } endpointId && _discovered.EndpointId == endpointId)
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
                    SpatialChoices.Add(new SpatialAudioOption(format, SpatialName(format)));
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

    internal static string SpatialName(Guid format)
    {
        return format == CoreAudio.SpatialAudioFormats.WindowsSonic ? "Windows Sonic"
            : format == CoreAudio.SpatialAudioFormats.DolbyAtmosForHeadphones ? "Dolby Atmos for Headphones"
            : format == CoreAudio.SpatialAudioFormats.DolbyAtmosForSpeakers ? "Dolby Atmos for Speakers"
            : format == CoreAudio.SpatialAudioFormats.DolbyAtmosForHomeTheater ? "Dolby Atmos for Home Theater"
            : format == CoreAudio.SpatialAudioFormats.DtsHeadphoneX ? "DTS Headphone:X"
            : format == CoreAudio.SpatialAudioFormats.DtsXUltra ? "DTS:X Ultra"
            : format.ToString();
    }
}

/// <summary>One named saved-endpoint choice.</summary>
public sealed record AudioEndpointOption(string Id, string Name)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}

/// <summary>One named default-format choice.</summary>
public sealed record AudioFormatOption(CoreAudio.AudioDeviceFormat Format)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Format.Channels} channels · {Format.SampleRate / 1000.0:0.#} kHz · {Format.BitsPerSample}-bit";
    }
}

/// <summary>One named spatial-audio-format choice.</summary>
public sealed record SpatialAudioOption(Guid Format, string Name)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
