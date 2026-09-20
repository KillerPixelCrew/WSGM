using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Settings;

namespace WSGM.Shell;

/// <summary>Projects live default-output format and spatial controls into Quick Access.</summary>
internal sealed class NativeQamAudioFormatService : ISteamAudioFormatBackend, IDisposable
{
    private readonly AudioManager _audio;
    private readonly AudioProfileService _profiles;

    private bool _disposed;

    // Exactly what the last publication offered, and the endpoint that offered it. A command is
    // admitted only against this: two endpoints can support the same format, so parsing the id is
    // not evidence that the row the user touched was describing the output that is default now.
    private volatile Offered? _offered;

    internal NativeQamAudioFormatService(AudioManager audio, AudioProfileService profiles)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _audio.PropertyChanged += OnAudioChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audio.PropertyChanged -= OnAudioChanged;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetFormatAsync(string formatId, CancellationToken cancellationToken)
    {
        if (_audio.SelectedOutput is not { } output
            || _offered is not { } offered
            || offered.EndpointId != output.Id
            || !offered.Formats.Contains(formatId)
            || !TryParseFormat(formatId, out var format))
        {
            return new SteamUiCommandResult(false, "That audio format is no longer available.");
        }

        var result = await _profiles.SetPlaybackFormatAsync(output.Id, format, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(result.Succeeded, result.Detail);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetSpatialAsync(string spatialId, CancellationToken cancellationToken)
    {
        if (_audio.SelectedOutput is not { } output
            || _offered is not { } offered
            || offered.EndpointId != output.Id
            || !offered.Spatial.Contains(spatialId)
            || !Guid.TryParse(spatialId, out var spatial))
        {
            return new SteamUiCommandResult(false, "That spatial sound format is no longer available.");
        }

        var result = await _profiles.SetSpatialFormatAsync(output.Id, spatial, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(result.Succeeded, result.Detail);
    }

    /// <summary>Raised when a changed default endpoint requires new options to be published.</summary>
    internal event Action? StateChanged;

    /// <summary>Reads the current output's bounded capability state.</summary>
    internal async ValueTask<SteamAudioFormatState?> ReadAsync()
    {
        var capabilities = await _profiles.ReadPlaybackCapabilitiesAsync(CancellationToken.None).ConfigureAwait(false);
        if (capabilities is null)
        {
            _offered = null;
            return new SteamAudioFormatState(false, [], string.Empty, [], string.Empty,
                "Advanced audio controls are unavailable for the current output.");
        }

        var formats = capabilities.SupportedFormats
            .Distinct()
            .Take(64)
            .Select(static format => new SteamAudioFormatOption(FormatId(format), FormatLabel(format)))
            .ToArray();
        var spatial = new[] { CoreAudio.SpatialAudioFormats.Off }
            .Concat(capabilities.SupportedSpatialFormats)
            .Distinct()
            .Take(16)
            .Select(static format =>
                new SteamAudioFormatOption(format.ToString(), AudioProfileEditor.SpatialName(format)))
            .ToArray();
        _offered = new Offered(
            capabilities.EndpointId,
            [.. formats.Select(static option => option.Id)],
            [.. spatial.Select(static option => option.Id)]);
        return new SteamAudioFormatState(
            formats.Length > 1 || spatial.Length > 1,
            formats,
            FormatId(capabilities.CurrentFormat),
            spatial,
            capabilities.CurrentSpatialFormat.ToString(),
            string.Empty);
    }

    private static string FormatId(CoreAudio.AudioDeviceFormat format)
    {
        return string.Join(':', format.Channels, format.SampleRate, format.BitsPerSample,
            format.ContainerBitsPerSample, format.ChannelMask, format.IsFloat ? 1 : 0);
    }

    private static string FormatLabel(CoreAudio.AudioDeviceFormat format)
    {
        return new AudioFormatOption(format).ToString();
    }

    private static bool TryParseFormat(string value, out CoreAudio.AudioDeviceFormat format)
    {
        format = default;
        var fields = value.Split(':');
        if (fields.Length != 6
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channels)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sampleRate)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bitsPerSample)
            || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var containerBitsPerSample)
            || !uint.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var channelMask)
            || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var floating))
        {
            return false;
        }

        format = new CoreAudio.AudioDeviceFormat(channels, sampleRate, bitsPerSample,
            containerBitsPerSample, channelMask, floating == 1);
        return floating is 0 or 1;
    }

    private void OnAudioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioManager.SelectedOutput))
        {
            StateChanged?.Invoke();
        }
    }

    private sealed record Offered(string EndpointId, HashSet<string> Formats, HashSet<string> Spatial);
}
