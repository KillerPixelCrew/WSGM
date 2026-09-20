using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Settings;

namespace WSGM.Shell;

/// <summary>Projects live default-output format and spatial controls into Quick Access.</summary>
internal sealed class NativeQamAudioFormatService : ISteamAudioFormatBackend, IDisposable
{
    private readonly AudioManager _audio;
    private readonly AudioProfileService _profiles;
    private bool _disposed;

    internal NativeQamAudioFormatService(AudioManager audio, AudioProfileService profiles)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _audio.PropertyChanged += OnAudioChanged;
    }

    /// <summary>Raised when a changed default endpoint requires new options to be published.</summary>
    internal event Action? StateChanged;

    /// <summary>Reads the current output's bounded capability state.</summary>
    internal async ValueTask<SteamAudioFormatState?> ReadAsync()
    {
        var capabilities = await _profiles.ReadPlaybackCapabilitiesAsync(CancellationToken.None).ConfigureAwait(false);
        if (capabilities is null)
        {
            return new SteamAudioFormatState(false, [], string.Empty, [], string.Empty,
                "Advanced audio controls are unavailable for the current output.");
        }

        var formats = capabilities.SupportedFormats
            .Distinct()
            .Take(64)
            .Select(static format => new SteamAudioFormatOption(FormatId(format), FormatLabel(format)))
            .ToArray();
        var spatial = new[] { WindowsDeviceControl.CoreAudio.SpatialAudioFormats.Off }
            .Concat(capabilities.SupportedSpatialFormats)
            .Distinct()
            .Take(16)
            .Select(static format => new SteamAudioFormatOption(format.ToString(), AudioProfileEditor.SpatialName(format)))
            .ToArray();
        return new SteamAudioFormatState(
            formats.Length > 1 || spatial.Length > 1,
            formats,
            FormatId(capabilities.CurrentFormat),
            spatial,
            capabilities.CurrentSpatialFormat.ToString(),
            string.Empty);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetFormatAsync(string formatId, CancellationToken cancellationToken)
    {
        if (_audio.SelectedOutput is not { } output || !TryParseFormat(formatId, out var format))
        {
            return new SteamUiCommandResult(false, "That audio format is no longer available.");
        }

        var result = await _profiles.SetPlaybackFormatAsync(output.Id, format, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(result.Succeeded, result.Detail);
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetSpatialAsync(string spatialId, CancellationToken cancellationToken)
    {
        if (_audio.SelectedOutput is not { } output || !Guid.TryParse(spatialId, out var spatial))
        {
            return new SteamUiCommandResult(false, "That spatial sound format is no longer available.");
        }

        var result = await _profiles.SetSpatialFormatAsync(output.Id, spatial, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(result.Succeeded, result.Detail);
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

    private static string FormatId(WindowsDeviceControl.CoreAudio.AudioDeviceFormat format)
    {
        return string.Join(':', format.Channels, format.SampleRate, format.BitsPerSample,
            format.ContainerBitsPerSample, format.ChannelMask, format.IsFloat ? 1 : 0);
    }

    private static string FormatLabel(WindowsDeviceControl.CoreAudio.AudioDeviceFormat format)
    {
        return new AudioFormatOption(format).ToString();
    }

    private static bool TryParseFormat(string value, out WindowsDeviceControl.CoreAudio.AudioDeviceFormat format)
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

        format = new WindowsDeviceControl.CoreAudio.AudioDeviceFormat(channels, sampleRate, bitsPerSample,
            containerBitsPerSample, channelMask, floating == 1);
        return floating is 0 or 1;
    }

    private void OnAudioChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioManager.SelectedOutput))
        {
            StateChanged?.Invoke();
        }
    }
}
