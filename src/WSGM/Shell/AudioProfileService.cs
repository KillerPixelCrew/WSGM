using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Observed playback format and spatial capabilities for one live output endpoint.</summary>
internal sealed record AudioPlaybackCapabilities(
    string EndpointId,
    IReadOnlyList<CoreAudio.AudioDeviceFormat> SupportedFormats,
    CoreAudio.AudioDeviceFormat CurrentFormat,
    IReadOnlyList<Guid> SupportedSpatialFormats,
    Guid CurrentSpatialFormat);

/// <summary>One outcome from applying an audio-profile value.</summary>
internal sealed record AudioProfileOperationResult(string Name, bool Succeeded, string Detail);

/// <summary>The observed outcome of one profile application.</summary>
internal sealed record AudioProfileApplyResult(IReadOnlyList<AudioProfileOperationResult> Operations)
{
    internal bool Succeeded => Operations.All(static operation => operation.Succeeded);
}

/// <summary>Core Audio calls the profile service needs, isolated for deterministic tests.</summary>
internal interface IAudioProfileOperations
{
    int ListEndpoints(CoreAudio.AudioDirection direction, out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints);

    int SetDefaultEndpoint(string endpointId);

    int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted);

    int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted);

    int SetMuted(bool muted);

    int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format);

    int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats);

    int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format);

    int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state);

    int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status);
}

/// <summary>Applies and observes Game Mode audio preferences away from the UI thread.</summary>
internal sealed class AudioProfileService : IAsyncDisposable
{
    private static readonly TimeSpan EndpointArrivalTimeout = TimeSpan.FromSeconds(3);

    private readonly AudioManager _audio;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAudioProfileOperations _operations;
    private bool _disposed;

    internal AudioProfileService(AudioManager audio, IAudioProfileOperations? operations = null)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _operations = operations ?? new CoreAudioProfileOperations();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    internal async Task<AudioProfilePreference?> CaptureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(Capture, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AudioProfileApplyResult> ApplyAsync(
        AudioProfilePreference? preference,
        CancellationToken cancellationToken)
    {
        if (preference is null)
        {
            return new AudioProfileApplyResult([]);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => Apply(preference, cancellationToken), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _audio.Refresh();
        }
    }

    internal async Task<AudioPlaybackCapabilities?> ReadPlaybackCapabilitiesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(ReadPlaybackCapabilities, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AudioProfileOperationResult> SetPlaybackFormatAsync(
        string endpointId,
        CoreAudio.AudioDeviceFormat format,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => SetPlaybackFormat(endpointId, format), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _audio.Refresh();
        }
    }

    internal async Task<AudioProfileOperationResult> SetSpatialFormatAsync(
        string endpointId,
        Guid format,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => SetSpatialFormat(endpointId, format), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _audio.Refresh();
        }
    }

    private AudioProfilePreference? Capture()
    {
        var outputs = List(CoreAudio.AudioDirection.Render);
        var inputs = List(CoreAudio.AudioDirection.Capture);
        var output = outputs.FirstOrDefault(static endpoint => endpoint.IsDefault);
        var input = inputs.FirstOrDefault(static endpoint => endpoint.IsDefault);
        if (output.Id.Length == 0 && input.Id.Length == 0)
        {
            return null;
        }

        var snapshot = new AudioProfilePreference
        {
            Output = output.Id.Length == 0 ? null : Endpoint(output),
            Input = input.Id.Length == 0 ? null : Endpoint(input)
        };

        if (output.Id.Length == 0)
        {
            return snapshot;
        }

        if (_operations.GetVolume(CoreAudio.AudioDirection.Render, out var volume, out var muted) >= 0)
        {
            snapshot.VolumePercent = volume;
            snapshot.Muted = muted != 0;
        }

        if (_operations.GetDeviceFormat(output.Id, out var deviceFormat) >= 0)
        {
            snapshot.PlaybackFormat = ToPreference(deviceFormat);
        }

        if (_operations.GetSpatialAudio(output.Id, out var spatial) >= 0)
        {
            snapshot.SpatialFormat = spatial.DefaultFormat;
        }

        return snapshot;
    }

    private AudioProfileApplyResult Apply(AudioProfilePreference preference, CancellationToken cancellationToken)
    {
        List<AudioProfileOperationResult> results = [];
        var output = ResolveEndpoint(preference.Output, CoreAudio.AudioDirection.Render, cancellationToken);
        var input = ResolveEndpoint(preference.Input, CoreAudio.AudioDirection.Capture, cancellationToken);
        if (preference.Output is not null)
        {
            results.Add(SetDefault("playback endpoint", output));
        }

        if (preference.Input is not null)
        {
            results.Add(SetDefault("recording endpoint", input));
        }

        if (preference.VolumePercent is { } volume)
        {
            var result = _operations.SetVolume(CoreAudio.AudioDirection.Render, volume, out _);
            results.Add(Result("playback volume", result));
        }

        if (preference.Muted is { } muted)
        {
            results.Add(Result("playback mute", _operations.SetMuted(muted)));
        }

        if (output is { } endpoint && preference.PlaybackFormat is { } format)
        {
            results.Add(SetPlaybackFormat(endpoint.Id, ToDeviceFormat(format)));
        }

        if (output is { } spatialEndpoint && preference.SpatialFormat is { } spatial)
        {
            results.Add(SetSpatialFormat(spatialEndpoint.Id, spatial));
        }

        return new AudioProfileApplyResult(results);
    }

    private AudioPlaybackCapabilities? ReadPlaybackCapabilities()
    {
        var output = List(CoreAudio.AudioDirection.Render).FirstOrDefault(static endpoint => endpoint.IsDefault);
        if (output.Id.Length == 0
            || _operations.GetDeviceFormat(output.Id, out var currentFormat) < 0
            || _operations.ListSupportedDeviceFormats(output.Id, out var supportedFormats) < 0
            || _operations.GetSpatialAudio(output.Id, out var spatial) < 0)
        {
            return null;
        }

        return new AudioPlaybackCapabilities(
            output.Id,
            supportedFormats,
            currentFormat,
            spatial.SupportedFormats,
            spatial.DefaultFormat);
    }

    private AudioProfileOperationResult SetPlaybackFormat(string endpointId, CoreAudio.AudioDeviceFormat format)
    {
        var capabilities = ReadPlaybackCapabilities();
        if (capabilities is null || capabilities.EndpointId != endpointId || !capabilities.SupportedFormats.Contains(format))
        {
            return new AudioProfileOperationResult("playback format", false,
                "The selected playback format is no longer supported by the default endpoint.");
        }

        return Result("playback format", _operations.SetDeviceFormat(endpointId, format));
    }

    private AudioProfileOperationResult SetSpatialFormat(string endpointId, Guid format)
    {
        var capabilities = ReadPlaybackCapabilities();
        if (capabilities is null
            || capabilities.EndpointId != endpointId
            || (format != CoreAudio.SpatialAudioFormats.Off && !capabilities.SupportedSpatialFormats.Contains(format)))
        {
            return new AudioProfileOperationResult("spatial sound", false,
                "The selected spatial format is no longer supported by the default endpoint.");
        }

        var result = _operations.SetSpatialAudio(endpointId, format, out var status);
        return result < 0
            ? Result("spatial sound", result)
            : status == CoreAudio.SpatialAudioSetStatus.Succeeded
                ? new AudioProfileOperationResult("spatial sound", true, "Applied.")
                : new AudioProfileOperationResult("spatial sound", false, $"Windows refused it: {status}.");
    }

    private CoreAudio.AudioEndpoint? ResolveEndpoint(
        AudioEndpointPreference? preference,
        CoreAudio.AudioDirection direction,
        CancellationToken cancellationToken)
    {
        if (preference?.Id is not { Length: > 0 } id)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + EndpointArrivalTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = List(direction).FirstOrDefault(endpoint => endpoint.Id == id);
            if (found.Id.Length > 0)
            {
                return found;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(200);
        } while (true);

        Log.Warn($"Audio profile: {direction} endpoint '{preference.Name ?? id}' is unavailable; leaving it unchanged.");
        return null;
    }

    private AudioProfileOperationResult SetDefault(string name, CoreAudio.AudioEndpoint? endpoint)
    {
        return endpoint is null
            ? new AudioProfileOperationResult(name, false, "The configured endpoint is unavailable.")
            : Result(name, _operations.SetDefaultEndpoint(endpoint.Value.Id));
    }

    private IReadOnlyList<CoreAudio.AudioEndpoint> List(CoreAudio.AudioDirection direction)
    {
        return _operations.ListEndpoints(direction, out var endpoints) >= 0 ? endpoints : [];
    }

    private static AudioEndpointPreference Endpoint(CoreAudio.AudioEndpoint endpoint)
    {
        return new AudioEndpointPreference { Id = endpoint.Id, Name = endpoint.Name };
    }

    internal static AudioFormatPreference ToPreference(CoreAudio.AudioDeviceFormat format)
    {
        return new AudioFormatPreference
        {
            Channels = format.Channels,
            SampleRate = format.SampleRate,
            BitsPerSample = format.BitsPerSample,
            ContainerBitsPerSample = format.ContainerBitsPerSample,
            ChannelMask = format.ChannelMask,
            IsFloat = format.IsFloat
        };
    }

    internal static CoreAudio.AudioDeviceFormat ToDeviceFormat(AudioFormatPreference format)
    {
        return new CoreAudio.AudioDeviceFormat(
            format.Channels,
            format.SampleRate,
            format.BitsPerSample,
            format.ContainerBitsPerSample,
            format.ChannelMask,
            format.IsFloat);
    }

    private static AudioProfileOperationResult Result(string name, int result)
    {
        return result >= 0
            ? new AudioProfileOperationResult(name, true, "Applied.")
            : new AudioProfileOperationResult(name, false, $"HRESULT 0x{result:X8}.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class CoreAudioProfileOperations : IAudioProfileOperations
    {
        public int ListEndpoints(CoreAudio.AudioDirection direction, out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints)
        {
            return CoreAudio.ListEndpoints(direction, out endpoints);
        }

        public int SetDefaultEndpoint(string endpointId) => CoreAudio.SetDefaultEndpoint(endpointId);

        public int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted) =>
            CoreAudio.GetVolume(direction, out volume, out muted);

        public int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted) =>
            CoreAudio.SetVolume(direction, volume, out muted);

        public int SetMuted(bool muted) => CoreAudio.SetMuted(muted);

        public int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format) =>
            CoreAudio.GetDeviceFormat(endpointId, out format);

        public int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats) =>
            CoreAudio.ListSupportedDeviceFormats(endpointId, out formats);

        public int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format) =>
            CoreAudio.SetDeviceFormat(endpointId, format);

        public int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state) =>
            CoreAudio.GetSpatialAudio(endpointId, out state);

        public int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status) =>
            CoreAudio.SetSpatialAudio(endpointId, format, out status);
    }
}
