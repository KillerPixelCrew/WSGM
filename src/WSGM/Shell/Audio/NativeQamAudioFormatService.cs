using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WindowsDeviceControl;

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

    /// <summary>Creates the audio projection and subscribes to default-output changes.</summary>
    /// <param name="audio">Borrowed session audio manager; disposal releases only this service's event subscription.</param>
    /// <param name="profiles">Borrowed owner of endpoint format capabilities, writes and persisted audio preferences.</param>
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
    public Task<SteamUiCommandResult> SetFormatAsync(string formatId, CancellationToken cancellationToken)
    {
        return SetFormatForEndpointAsync(_offered?.EndpointId ?? string.Empty, formatId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> SetSpatialAsync(string spatialId, CancellationToken cancellationToken)
    {
        return SetSpatialForEndpointAsync(_offered?.EndpointId ?? string.Empty, spatialId, cancellationToken);
    }

    /// <summary>Applies a native Settings selection only to the output its row described.</summary>
    /// <param name="endpointId">Endpoint id published with the row; must still name the default output.</param>
    /// <param name="formatId">Exact id in the last published format/channel choices.</param>
    /// <param name="cancellationToken">Cancels the profile owner's endpoint write.</param>
    /// <returns>The write result, or a refusal if the endpoint or offered choice has changed.</returns>
    internal async Task<SteamUiCommandResult> SetFormatForEndpointAsync(string endpointId, string formatId,
        CancellationToken cancellationToken)
    {
        if (_audio.SelectedOutput is not { } output
            || _offered is not { } offered
            || endpointId != output.Id
            || offered.EndpointId != output.Id
            || !offered.Formats.Contains(formatId)
            || !TryParseFormat(formatId, out var format))
        {
            return new SteamUiCommandResult(false, "That audio format is no longer available.");
        }

        var result = await _profiles.SetPlaybackFormatAsync(output.Id, format, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke();
        return new SteamUiCommandResult(result.Succeeded, result.Detail);
    }

    /// <summary>Applies a native Settings selection only to the output its row described.</summary>
    /// <param name="endpointId">Endpoint id published with the row; must still name the default output.</param>
    /// <param name="spatialId">GUID string from the last published spatial-format choices.</param>
    /// <param name="cancellationToken">Cancels the profile owner's endpoint write.</param>
    /// <returns>The write result, or a refusal if the endpoint or offered choice has changed.</returns>
    internal async Task<SteamUiCommandResult> SetSpatialForEndpointAsync(string endpointId, string spatialId,
        CancellationToken cancellationToken)
    {
        if (_audio.SelectedOutput is not { } output
            || _offered is not { } offered
            || endpointId != output.Id
            || offered.EndpointId != output.Id
            || !offered.Spatial.Contains(spatialId)
            || !Guid.TryParse(spatialId, out var spatial))
        {
            return new SteamUiCommandResult(false, "That spatial sound format is no longer available.");
        }

        var result = await _profiles.SetSpatialFormatAsync(output.Id, spatial, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke();
        return new SteamUiCommandResult(result.Succeeded, result.Detail);
    }

    /// <summary>Raised when a changed default endpoint requires new options to be published.</summary>
    internal event Action? StateChanged;

    /// <summary>Reads the current output's format and spatial capabilities.</summary>
    /// <returns>The current choices and selection, or an unavailable state with a reason when the endpoint cannot be read.</returns>
    internal async ValueTask<SteamAudioFormatState?> ReadAsync()
    {
        return (await ReadEndpointAsync().ConfigureAwait(false)).State;
    }

    /// <summary>Returns choices together with the exact endpoint that offered them.</summary>
    /// <returns>
    ///     Capabilities paired with their endpoint identity; an unavailable state and null id clear command admission
    ///     when no endpoint can be read.
    /// </returns>
    internal async ValueTask<(SteamAudioFormatState State, string? EndpointId)> ReadEndpointAsync()
    {
        var capabilities = await _profiles.ReadPlaybackCapabilitiesAsync(CancellationToken.None).ConfigureAwait(false);
        if (capabilities is null)
        {
            _offered = null;
            return (new SteamAudioFormatState(false, [], string.Empty, [], string.Empty, [], string.Empty,
                "Advanced audio controls are unavailable for the current output."), null);
        }

        var channels = AudioPlaybackChoices.Channels(capabilities)
            .Select(choice => new SteamAudioFormatOption(FormatId(choice.Format), choice.Label)).ToArray();
        var formats = AudioPlaybackChoices.Formats(capabilities)
            .Select(choice => new SteamAudioFormatOption(FormatId(choice.Format), choice.Label)).ToArray();
        var spatial = new[] { CoreAudio.SpatialAudioFormats.Off }
            .Concat(capabilities.SupportedSpatialFormats)
            .Distinct()
            .Select(static format =>
                new SteamAudioFormatOption(format.ToString(), SpatialAudioNames.For(format)))
            .ToArray();
        _offered = new Offered(
            capabilities.EndpointId,
            [.. channels.Concat(formats).Select(static option => option.Id)],
            [.. spatial.Select(static option => option.Id)]);
        return (new SteamAudioFormatState(
            channels.Length > 0 || formats.Length > 0 || spatial.Length > 0,
            channels,
            FormatId(capabilities.CurrentFormat),
            formats,
            FormatId(capabilities.CurrentFormat),
            spatial,
            capabilities.CurrentSpatialFormat.ToString(),
            string.Empty), capabilities.EndpointId);
    }

    private static string FormatId(CoreAudio.AudioDeviceFormat format)
    {
        return string.Join(':', format.Channels, format.SampleRate, format.BitsPerSample,
            format.ContainerBitsPerSample, format.ChannelMask, format.IsFloat ? 1 : 0);
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
