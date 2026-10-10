using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Observed playback format and spatial capabilities for one live output endpoint.</summary>
/// <param name="EndpointId">Default playback endpoint observed during this read.</param>
/// <param name="SupportedFormats">Finite set of formats successfully probed for this endpoint.</param>
/// <param name="CurrentFormat">Device format observed before the probes.</param>
/// <param name="SupportedSpatialFormats">
///     Spatial formats reported by Windows; support does not guarantee licence
///     availability.
/// </param>
/// <param name="CurrentSpatialFormat">Observed spatial format, including the Off sentinel.</param>
internal sealed record AudioPlaybackCapabilities(
    string EndpointId,
    IReadOnlyList<CoreAudio.AudioDeviceFormat> SupportedFormats,
    CoreAudio.AudioDeviceFormat CurrentFormat,
    IReadOnlyList<Guid> SupportedSpatialFormats,
    Guid CurrentSpatialFormat);

/// <summary>One outcome from applying an audio-profile value.</summary>
/// <param name="Name">User-facing name of the attempted setting.</param>
/// <param name="Succeeded">Whether Windows accepted this operation; no confirming readback is required.</param>
/// <param name="Detail">Outcome or refusal detail suitable for display.</param>
internal sealed record AudioProfileOperationResult(string Name, bool Succeeded, string Detail);

/// <summary>Operation results from one profile application; partial writes are not rolled back.</summary>
/// <param name="Operations">Outcomes for attempted settings, in application order; an empty list is successful.</param>
internal sealed record AudioProfileApplyResult(IReadOnlyList<AudioProfileOperationResult> Operations)
{
    /// <summary>Whether every attempted setting succeeded; true when no settings were requested.</summary>
    internal bool Succeeded => Operations.All(static operation => operation.Succeeded);
}

/// <summary>Core Audio operations used by the profile service.</summary>
/// <remarks>
///     Methods return HRESULTs: negative values indicate failure and make out values unusable unless documented
///     otherwise.
/// </remarks>
internal interface IAudioProfileOperations
{
    /// <summary>Lists active endpoints in one direction.</summary>
    /// <param name="direction">Playback or recording flow.</param>
    /// <param name="endpoints">Detached endpoint snapshot on success.</param>
    /// <returns>The native HRESULT.</returns>
    int ListEndpoints(CoreAudio.AudioDirection direction, out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints);

    /// <summary>Sets the named endpoint as default for all audio roles.</summary>
    /// <param name="endpointId">Active endpoint identifier; a partial role change is possible on failure.</param>
    /// <returns>The native HRESULT.</returns>
    int SetDefaultEndpoint(string endpointId);

    /// <summary>Reads the console-role default endpoint’s volume and mute state.</summary>
    /// <param name="direction">Playback or recording flow.</param>
    /// <param name="volume">Rounded volume percentage on success.</param>
    /// <param name="muted">One when muted; zero otherwise, on success.</param>
    /// <returns>The native HRESULT.</returns>
    int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted);

    /// <summary>Sets the console-role default endpoint’s volume.</summary>
    /// <param name="direction">Playback or recording flow.</param>
    /// <param name="volume">Requested percentage, clamped to 0–100.</param>
    /// <param name="muted">Mute state after the write, valid only on success. A positive volume also unmutes the endpoint.</param>
    /// <returns>The native HRESULT.</returns>
    int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted);

    /// <summary>Sets mute on the console-role default playback endpoint.</summary>
    /// <param name="muted">Requested mute state.</param>
    /// <returns>The native HRESULT.</returns>
    int SetMuted(bool muted);

    /// <summary>Reads an endpoint’s shared-mode device format.</summary>
    /// <param name="endpointId">Endpoint identifier.</param>
    /// <param name="format">Observed format on success.</param>
    /// <returns>The native HRESULT.</returns>
    int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format);

    /// <summary>Probes the supported candidate formats for an endpoint.</summary>
    /// <param name="endpointId">Endpoint identifier.</param>
    /// <param name="formats">Supported candidates on success; not an exhaustive hardware format list.</param>
    /// <returns>The native HRESULT.</returns>
    int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats);

    /// <summary>Requests a device format change.</summary>
    /// <param name="endpointId">Endpoint identifier.</param>
    /// <param name="format">Requested format; callers must check support.</param>
    /// <returns>The native HRESULT.</returns>
    int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format);

    /// <summary>Reads supported spatial formats and the current selection.</summary>
    /// <param name="endpointId">Playback endpoint identifier.</param>
    /// <param name="state">Observed spatial state on success.</param>
    /// <returns>The native HRESULT.</returns>
    int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state);

    /// <summary>Requests a spatial format change.</summary>
    /// <param name="endpointId">Playback endpoint identifier.</param>
    /// <param name="format">Requested spatial format or Off sentinel.</param>
    /// <param name="status">Windows operation status, valid only for a nonnegative HRESULT; inspect it separately.</param>
    /// <returns>The native HRESULT.</returns>
    int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status);

    /// <summary>Registers endpoint-change notifications.</summary>
    /// <param name="onChanged">Short nonblocking callback; may run on a native notification thread.</param>
    /// <param name="watch">Caller-owned registration on success; dispose to unregister.</param>
    /// <returns>The native HRESULT.</returns>
    int WatchEndpoints(Action onChanged, out IDisposable? watch);
}

/// <summary>Applies and observes Game Mode audio preferences away from the UI thread.</summary>
internal sealed class AudioProfileService : IAsyncDisposable
{
    private const string UnselectedPlayback =
        "The configured playback endpoint is not the default, so this was left unchanged.";

    private static readonly TimeSpan EndpointArrivalTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAudioProfileOperations _operations;

    private readonly Action? _refresh;
    private bool _disposed;

    /// <summary>Creates the service over explicit Core Audio operations.</summary>
    /// <param name="operations">The Core Audio calls; production passes <see cref="CoreAudioProfileOperations" />.</param>
    /// <param name="audio">The live audio manager refreshed after each change, or null in recovery.</param>
    internal AudioProfileService(IAudioProfileOperations operations, AudioManager? audio = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _refresh = audio is null ? null : audio.Refresh;
    }

    /// <summary>Closes admission and waits for the currently serialized operation to finish.</summary>
    /// <returns>Completion after the operation gate is available; borrowed dependencies are not disposed.</returns>
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

    /// <summary>Captures readable defaults, volume and format settings for desktop recovery.</summary>
    /// <param name="cancellationToken">Cancels waiting or worker admission; does not interrupt a running native query.</param>
    /// <returns>A partial preference containing only readable values, or null when neither default endpoint can be read.</returns>
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

    /// <summary>Serializes audio preference writes, preserving unspecified settings.</summary>
    /// <param name="preference">Requested settings, or null for no work.</param>
    /// <param name="cancellationToken">
    ///     Cancels gate and endpoint-arrival waits. Native writes already started are not
    ///     interrupted or rolled back.
    /// </param>
    /// <returns>Per-setting acceptance results; a successful result is not a confirming readback.</returns>
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
            _refresh?.Invoke();
        }
    }

    /// <summary>Reads the current playback endpoint’s format and spatial capabilities under the operation gate.</summary>
    /// <param name="cancellationToken">Cancels waiting or worker admission.</param>
    /// <returns>The endpoint snapshot, or null when any required capability query fails.</returns>
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

    /// <summary>Writes a supported format only if the named endpoint is still the default playback device.</summary>
    /// <param name="endpointId">Default endpoint identity captured by the UI.</param>
    /// <param name="format">Shared-mode device format.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation gate; the admitted native write runs to completion.</param>
    /// <returns>Windows acceptance or a refusal; no matching readback is performed.</returns>
    internal async Task<AudioProfileOperationResult> SetPlaybackFormatAsync(
        string endpointId,
        CoreAudio.AudioDeviceFormat format,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => SetPlaybackFormat(endpointId, format, ReadPlaybackCapabilities()),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _refresh?.Invoke();
        }
    }

    /// <summary>Writes a supported format only if the named endpoint is still the default playback device.</summary>
    /// <param name="endpointId">Default endpoint identity captured by the UI.</param>
    /// <param name="format">Spatial format identifier or Off sentinel.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation gate; the admitted native write runs to completion.</param>
    /// <returns>Windows acceptance or a refusal; no matching readback is performed.</returns>
    internal async Task<AudioProfileOperationResult> SetSpatialFormatAsync(
        string endpointId,
        Guid format,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(() => SetSpatialFormat(endpointId, format, ReadPlaybackCapabilities()),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _refresh?.Invoke();
        }
    }

    private AudioProfilePreference? Capture()
    {
        var outputs = List(CoreAudio.AudioDirection.Render);
        var inputs = List(CoreAudio.AudioDirection.Capture);
        var output = outputs.FirstOrDefault(static endpoint => endpoint.IsDefault);
        var input = inputs.FirstOrDefault(static endpoint => endpoint.IsDefault);
        // A failed enumeration reads as an empty list, and the default AudioEndpoint that comes
        // back from it carries a null Id, so every identity check here has to tolerate one.
        if (string.IsNullOrEmpty(output.Id) && string.IsNullOrEmpty(input.Id))
        {
            return null;
        }

        var snapshot = new AudioProfilePreference
        {
            Output = string.IsNullOrEmpty(output.Id) ? null : Endpoint(output),
            Input = string.IsNullOrEmpty(input.Id) ? null : Endpoint(input)
        };

        if (string.IsNullOrEmpty(output.Id))
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
        // One arrival budget for the transition, not one per direction: two missing endpoints
        // would otherwise hold the desktop return for twice the bounded wait.
        var deadline = Environment.TickCount64 + (long)EndpointArrivalTimeout.TotalMilliseconds;
        var output = ResolveEndpoint(preference.Output, CoreAudio.AudioDirection.Render, deadline, cancellationToken);
        var input = ResolveEndpoint(preference.Input, CoreAudio.AudioDirection.Capture, deadline, cancellationToken);
        if (preference.Output is null &&
            (preference.PlaybackFormat is not null || preference.SpatialFormat is not null))
        {
            var currentDefault = List(CoreAudio.AudioDirection.Render).FirstOrDefault(endpoint => endpoint.IsDefault);
            output = string.IsNullOrEmpty(currentDefault.Id) ? null : currentDefault;
        }

        // Volume and mute are written through whatever is default now. When the profile asked for
        // a particular playback endpoint and that endpoint did not become the default, the write
        // would land on an unrelated device the same result set has just reported untouched.
        var playbackSelected = true;
        if (preference.Output is not null)
        {
            var result = SetDefault("playback endpoint", output);
            playbackSelected = result.Succeeded;
            results.Add(result);
        }

        if (preference.Input is not null)
        {
            results.Add(SetDefault("recording endpoint", input));
        }

        if (preference.VolumePercent is { } volume)
        {
            results.Add(playbackSelected
                ? Result("playback volume", _operations.SetVolume(CoreAudio.AudioDirection.Render, volume, out _))
                : new AudioProfileOperationResult("playback volume", false, UnselectedPlayback));
        }

        if (preference.Muted is { } muted)
        {
            results.Add(playbackSelected
                ? Result("playback mute", _operations.SetMuted(muted))
                : new AudioProfileOperationResult("playback mute", false, UnselectedPlayback));
        }

        if (preference.PlaybackFormat is { } format)
        {
            results.Add(output is { } endpoint && playbackSelected
                ? SetPlaybackFormat(endpoint.Id, ToDeviceFormat(format), ReadPlaybackCapabilities())
                : new AudioProfileOperationResult("playback format", false, UnselectedPlayback));
        }

        if (preference.SpatialFormat is { } spatial)
        {
            // Read after the format write: the spatial formats an endpoint supports can change with its
            // device format, so an earlier read would check against stale capabilities.
            results.Add(output is { } spatialEndpoint && playbackSelected
                ? SetSpatialFormat(spatialEndpoint.Id, spatial, ReadPlaybackCapabilities())
                : new AudioProfileOperationResult("spatial sound", false, UnselectedPlayback));
        }

        return new AudioProfileApplyResult(results);
    }

    private AudioPlaybackCapabilities? ReadPlaybackCapabilities()
    {
        var output = List(CoreAudio.AudioDirection.Render).FirstOrDefault(static endpoint => endpoint.IsDefault);
        if (string.IsNullOrEmpty(output.Id)
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

    private AudioProfileOperationResult SetPlaybackFormat(string endpointId, CoreAudio.AudioDeviceFormat format,
        AudioPlaybackCapabilities? capabilities)
    {
        if (capabilities is null || capabilities.EndpointId != endpointId ||
            !capabilities.SupportedFormats.Contains(format))
        {
            return new AudioProfileOperationResult("playback format", false,
                "The selected playback format is no longer supported by the default endpoint.");
        }

        return Result("playback format", _operations.SetDeviceFormat(endpointId, format));
    }

    private AudioProfileOperationResult SetSpatialFormat(string endpointId, Guid format,
        AudioPlaybackCapabilities? capabilities)
    {
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
        long deadline,
        CancellationToken cancellationToken)
    {
        if (preference?.Id is not { Length: > 0 } id)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Find(direction, id) is { } present)
        {
            return present;
        }

        // Core Audio reports the arrival (or the state change of an HDMI endpoint as the TV wakes),
        // so the wait ends on that notification. The watch is registered before the next listing,
        // so an endpoint that appears between the two still wakes it.
        using var arrived = new SemaphoreSlim(0);
        if (_operations.WatchEndpoints(() => arrived.Release(), out var watch) >= 0)
        {
            using (watch)
            {
                do
                {
                    if (Find(direction, id) is { } found)
                    {
                        return found;
                    }

                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    // Cancellation ends the wait at once by throwing.
                    arrived.Wait(TimeSpan.FromMilliseconds(remaining), cancellationToken);
                } while (true);
            }
        }

        Log.Warn(
            $"Audio profile: {direction} endpoint '{(string.IsNullOrEmpty(preference.Name) ? id : preference.Name)}' is unavailable; leaving it unchanged.");
        return null;
    }

    private CoreAudio.AudioEndpoint? Find(CoreAudio.AudioDirection direction, string id)
    {
        var found = List(direction).FirstOrDefault(endpoint => endpoint.Id == id);
        return string.IsNullOrEmpty(found.Id) ? null : found;
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
        return new AudioEndpointPreference { Id = endpoint.Id, Name = AudioEndpointText.Name(endpoint) };
    }

    /// <summary>Copies a native format into its serializable preference representation.</summary>
    /// <param name="format">Native format to copy.</param>
    /// <returns>A new preference with the same channel, sample and container values.</returns>
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

    /// <summary>Converts a saved format without probing endpoint support.</summary>
    /// <param name="format">Preference to convert.</param>
    /// <returns>The corresponding native format value.</returns>
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
}

/// <summary>The production <see cref="IAudioProfileOperations" /> over Windows Core Audio.</summary>
internal sealed class CoreAudioProfileOperations : IAudioProfileOperations
{
    /// <inheritdoc />
    public int ListEndpoints(CoreAudio.AudioDirection direction,
        out IReadOnlyList<CoreAudio.AudioEndpoint> endpoints)
    {
        return CoreAudio.ListEndpoints(direction, out endpoints);
    }

    /// <inheritdoc />
    public int SetDefaultEndpoint(string endpointId)
    {
        return CoreAudio.SetDefaultEndpoint(endpointId);
    }

    /// <inheritdoc />
    public int GetVolume(CoreAudio.AudioDirection direction, out int volume, out int muted)
    {
        return CoreAudio.GetVolume(direction, out volume, out muted);
    }

    /// <inheritdoc />
    public int SetVolume(CoreAudio.AudioDirection direction, int volume, out int muted)
    {
        return CoreAudio.SetVolume(direction, volume, out muted);
    }

    /// <inheritdoc />
    public int SetMuted(bool muted)
    {
        return CoreAudio.SetMuted(muted);
    }

    /// <inheritdoc />
    public int GetDeviceFormat(string endpointId, out CoreAudio.AudioDeviceFormat format)
    {
        return CoreAudio.GetDeviceFormat(endpointId, out format);
    }

    /// <inheritdoc />
    public int ListSupportedDeviceFormats(string endpointId, out IReadOnlyList<CoreAudio.AudioDeviceFormat> formats)
    {
        return CoreAudio.ListSupportedDeviceFormats(endpointId, out formats);
    }

    /// <inheritdoc />
    public int SetDeviceFormat(string endpointId, CoreAudio.AudioDeviceFormat format)
    {
        return CoreAudio.SetDeviceFormat(endpointId, format);
    }

    /// <inheritdoc />
    public int GetSpatialAudio(string endpointId, out CoreAudio.SpatialAudioState state)
    {
        return CoreAudio.GetSpatialAudio(endpointId, out state);
    }

    /// <inheritdoc />
    public int SetSpatialAudio(string endpointId, Guid format, out CoreAudio.SpatialAudioSetStatus status)
    {
        return CoreAudio.SetSpatialAudio(endpointId, format, out status);
    }

    /// <inheritdoc />
    public int WatchEndpoints(Action onChanged, out IDisposable? watch)
    {
        return CoreAudio.StartEndpointWatch(_ => onChanged(), out watch);
    }
}
