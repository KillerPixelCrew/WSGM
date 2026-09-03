using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
/// Projects <see cref="AudioManager"/> into the state Steam's audio surface renders, and answers
/// its writes.
/// </summary>
/// <remarks>
/// The backend already exists and is the same one the custom taskbar drives, so this is an adapter
/// rather than an implementation. Keeping it an adapter is the point: a second audio path would
/// eventually disagree with the taskbar about which endpoint is default. The shape Steam sees and
/// the mapping into Steam's own field names are the toolkit's (<see cref="SteamAudioSurface"/>).
/// </remarks>
internal sealed class AudioManagerNativeQamAudioService : ISteamAudioBackend, IDisposable
{
    private readonly AudioManager _audio;
    private readonly object _gate = new();
    private SteamAudioState _current;
    private bool _disposed;

    /// <summary>Adapts a running audio manager.</summary>
    /// <param name="audio">The manager, already started.</param>
    public AudioManagerNativeQamAudioService(AudioManager audio)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _current = Project(audio);
        _audio.PropertyChanged += OnAudioChanged;
        _audio.OutputEndpoints.CollectionChanged += OnEndpointsChanged;
        _audio.InputEndpoints.CollectionChanged += OnEndpointsChanged;
    }

    /// <summary>Raised when the projected state changes.</summary>
    public event Action? StateChanged;

    /// <summary>The state Steam should currently be rendering.</summary>
    public SteamAudioState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetDefaultDeviceAsync(
        string deviceId,
        bool input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Log.Warn("Native QAM audio: default device change refused; no endpoint named.");
            return new SteamUiCommandResult(false, "No audio device was named.");
        }

        AudioEndpointEntry? entry = null;
        await NativeQamUi.RunAsync(() =>
        {
            entry = (input ? _audio.InputEndpoints : _audio.OutputEndpoints)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));
            if (entry is null)
            {
                return;
            }

            if (input)
            {
                _audio.SelectedInput = entry;
            }
            else
            {
                _audio.SelectedOutput = entry;
            }

            Publish();
        }, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            Log.Warn(
                $"Native QAM audio: '{deviceId}' is not a known "
                + $"{(input ? "input" : "output")} endpoint.");
            return new SteamUiCommandResult(false, "That audio device is no longer present.");
        }

        Log.Info(
            $"Native QAM audio: default {(input ? "input" : "output")} set to '{entry.Name}'.");
        return new SteamUiCommandResult(true, string.Empty);
    }

    /// <summary>Sets the system volume.</summary>
    /// <param name="percent">Target volume, 0-100.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The outcome, as the native QAM reports outcomes.</returns>
    public async Task<SteamUiCommandResult> SetVolumeAsync(
        int percent,
        CancellationToken cancellationToken
    ) => await SetVolumeAsync(percent, input: false, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetVolumeAsync(
        int percent,
        bool input,
        CancellationToken cancellationToken)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        if (clamped != percent)
        {
            Log.Info(
                $"Native QAM audio: {(input ? "microphone " : string.Empty)}volume "
                    + $"{percent} clamped to {clamped}.");
        }

        await NativeQamUi.RunAsync(() =>
        {
            if (input)
            {
                _audio.InputVolumePercent = clamped;
            }
            else
            {
                _audio.VolumePercent = clamped;
            }
            Publish();
        }, cancellationToken).ConfigureAwait(false);
        return new SteamUiCommandResult(true, string.Empty);
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
        _audio.OutputEndpoints.CollectionChanged -= OnEndpointsChanged;
        _audio.InputEndpoints.CollectionChanged -= OnEndpointsChanged;
    }

    /// <summary>
    /// Builds the state Steam should render from the manager's current view.
    /// </summary>
    /// <param name="audio">The manager to read.</param>
    /// <returns>The projected state.</returns>
    /// <remarks>
    /// An endpoint present in both directions is reported once carrying both flags, because Steam's
    /// device model is one entry with a direction test rather than two entries. Listing it twice
    /// would put the same hardware in the picker under two identities.
    /// </remarks>
    internal static SteamAudioState Project(AudioManager audio)
    {
        Dictionary<string, SteamAudioDevice> byId = new(StringComparer.Ordinal);
        foreach (AudioEndpointEntry entry in audio.OutputEndpoints)
        {
            byId[entry.Id] = new SteamAudioDevice(entry.Id, entry.Name, true, false);
        }

        foreach (AudioEndpointEntry entry in audio.InputEndpoints)
        {
            byId[entry.Id] = byId.TryGetValue(entry.Id, out SteamAudioDevice? existing)
                ? existing with { HasInput = true }
                : new SteamAudioDevice(entry.Id, entry.Name, false, true);
        }

        bool available = byId.Count > 0;
        return new SteamAudioState(
            available,
            [.. byId.Values],
            audio.SelectedOutput?.Id ?? string.Empty,
            audio.SelectedInput?.Id ?? string.Empty,
            (int)Math.Round(audio.VolumePercent),
            audio.Muted,
            audio.InputVolumePercent is { } inputVolume
                ? (int)Math.Round(inputVolume)
                : null,
            audio.InputMuted,
            available ? audio.ErrorText : "No audio endpoints are present.");
    }

    private void OnAudioChanged(object? sender, PropertyChangedEventArgs e) => Publish();

    private void OnEndpointsChanged(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        SteamAudioState next = Project(_audio);
        lock (_gate)
        {
            if (Same(_current, next))
            {
                return;
            }

            _current = next;
        }

        StateChanged?.Invoke();
    }

    /// <remarks>
    /// The device list has to be compared element by element. Record equality would compare it by
    /// reference, and <see cref="Project"/> builds a fresh list every time, so every property change
    /// on the manager — including a volume tick — would look like a change to the whole device set
    /// and push a redundant update into Steam.
    /// </remarks>
    private static bool Same(SteamAudioState left, SteamAudioState right) =>
        left.Available == right.Available
        && left.VolumePercent == right.VolumePercent
        && left.Muted == right.Muted
        && left.InputVolumePercent == right.InputVolumePercent
        && left.InputMuted == right.InputMuted
        && string.Equals(left.ActiveOutputDeviceId, right.ActiveOutputDeviceId, StringComparison.Ordinal)
        && string.Equals(left.ActiveInputDeviceId, right.ActiveInputDeviceId, StringComparison.Ordinal)
        && string.Equals(left.StatusText, right.StatusText, StringComparison.Ordinal)
        && left.Devices.SequenceEqual(right.Devices);
}
