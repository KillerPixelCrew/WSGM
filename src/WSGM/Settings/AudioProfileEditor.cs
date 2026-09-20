using System;
using System.Collections.ObjectModel;
using System.Linq;
using WindowsDeviceControl;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Settings;

/// <summary>Editable saved audio preferences for one display-switch direction.</summary>
public sealed class AudioProfileEditor : ObservableObject
{
    private readonly Action _changed;
    private bool _applyMute;
    private bool _applyVolume;
    private AudioFormatOption? _format;
    private AudioEndpointOption? _input;
    private bool _loading;
    private bool _muted;
    private AudioEndpointOption? _output;
    private SpatialAudioOption? _spatial;
    private int _volumePercent = 50;

    internal AudioProfileEditor(Action changed)
    {
        _changed = changed;
        RefreshEndpoints();
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

            RefreshPlaybackCapabilities();
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
            if (SetFieldIfChanged(ref _format, value, nameof(PlaybackFormat)))
            {
                Edited();
            }
        }
    }

    /// <summary>Selected spatial format, or null to leave it unchanged.</summary>
    public SpatialAudioOption? SpatialFormat
    {
        get => _spatial;
        set
        {
            if (SetFieldIfChanged(ref _spatial, value, nameof(SpatialFormat)))
            {
                Edited();
            }
        }
    }

    /// <summary>Reloads live endpoint and capability choices without writing Windows audio state.</summary>
    public void RefreshEndpoints()
    {
        var preferredOutput = _output?.Id;
        var preferredInput = _input?.Id;
        OutputChoices.Clear();
        InputChoices.Clear();
        if (CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Render, out var outputs) >= 0)
        {
            foreach (var endpoint in outputs)
            {
                OutputChoices.Add(new AudioEndpointOption(endpoint.Id, endpoint.Name));
            }
        }

        if (CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Capture, out var inputs) >= 0)
        {
            foreach (var endpoint in inputs)
            {
                InputChoices.Add(new AudioEndpointOption(endpoint.Id, endpoint.Name));
            }
        }

        _loading = true;
        _output = OutputChoices.FirstOrDefault(choice => choice.Id == preferredOutput);
        _input = InputChoices.FirstOrDefault(choice => choice.Id == preferredInput);
        _loading = false;
        RefreshPlaybackCapabilities();
        Raise(nameof(Output));
        Raise(nameof(Input));
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
        RefreshPlaybackCapabilities();
        _format = FormatChoices.FirstOrDefault(choice => Same(choice.Format, preference?.PlaybackFormat));
        _spatial = SpatialChoices.FirstOrDefault(choice => choice.Format == preference?.SpatialFormat);
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
            PlaybackFormat = _format is null ? null : AudioProfileService.ToPreference(_format.Format),
            SpatialFormat = _spatial?.Format
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

    private void RefreshPlaybackCapabilities()
    {
        FormatChoices.Clear();
        SpatialChoices.Clear();
        _format = null;
        _spatial = null;
        if (_output?.Id is not { } endpointId)
        {
            Raise(nameof(PlaybackFormat));
            Raise(nameof(SpatialFormat));
            return;
        }

        if (CoreAudio.ListSupportedDeviceFormats(endpointId, out var formats) >= 0)
        {
            foreach (var format in formats)
            {
                FormatChoices.Add(new AudioFormatOption(format));
            }
        }

        if (CoreAudio.GetSpatialAudio(endpointId, out var spatial) >= 0)
        {
            SpatialChoices.Add(new SpatialAudioOption(CoreAudio.SpatialAudioFormats.Off, "Off"));
            foreach (var format in spatial.SupportedFormats)
            {
                SpatialChoices.Add(new SpatialAudioOption(format, SpatialName(format)));
            }
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
    public override string ToString() => Name;
}

/// <summary>One named default-format choice.</summary>
public sealed record AudioFormatOption(CoreAudio.AudioDeviceFormat Format)
{
    /// <inheritdoc />
    public override string ToString() => $"{Format.Channels} channels · {Format.SampleRate / 1000.0:0.#} kHz · {Format.BitsPerSample}-bit";
}

/// <summary>One named spatial-audio-format choice.</summary>
public sealed record SpatialAudioOption(Guid Format, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
