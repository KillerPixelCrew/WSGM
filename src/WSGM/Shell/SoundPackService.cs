using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

internal sealed record SoundPackState(
    SoundPack[] Packs,
    string Selected,
    bool Busy,
    string? Error,
    string Compatibility,
    ThemeStoreSummary[] Listings,
    int Total,
    int Page)
{
    internal string Integration { get; init; } = "Waiting for Steam sound integration.";
}

/// <summary>Session-owned sound content, serialized commands and detached Steam publications.</summary>
internal sealed class SoundPackService : IChangeSource, IAsyncDisposable
{
    private readonly SoundPackLibrary _library;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _previewSync = new();
    private readonly Func<string> _readSelected;
    private readonly Action<string> _report;
    private readonly Action<string> _saveSelected;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Func<string?> _steamDirectory;
    private readonly ThemeStoreClient _store;
    private readonly object _sync = new();
    private bool _disposed;
    private bool _hostConnected;
    private SteamSoundOverrideState _overrides = new(new Dictionary<string, IReadOnlyList<string>>());
    private SteamSoundOverrideStatus? _playbackStatus;
    private long _playingPreviewEpoch;
    private AudioFilePreview? _preview;
    private long _previewEpoch;
    private long _revision;
    private SoundPackState _state = new([], "", false, null, "Not checked yet.", [], 0, 0);

    internal SoundPackService(SoundPackLibrary library, Func<string> readSelected, Action<string> saveSelected,
        Func<string?> steamDirectory, Action<string>? report = null, ThemeStoreClient? store = null)
    {
        _library = library;
        _readSelected = readSelected;
        _saveSelected = saveSelected;
        _steamDirectory = steamDirectory;
        _shutdownToken = _shutdown.Token;
        _report = report ?? Log.Warn;
        _store = store ?? new ThemeStoreClient();
        _state = _state with { Selected = readSelected() };
    }

    internal long Revision => Interlocked.Read(ref _revision);

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_previewSync)
            {
                _preview?.Dispose();
                _preview = null;
            }
        }
        finally
        {
            _operations.Release();
            _shutdown.Dispose();
        }
    }

    public event Action? Changed;

    /// <summary>Says what Steam's sound integration is doing, from the host switch and the override patch.</summary>
    /// <param name="hostOn">Whether WSGM's own Steam surfaces are on.</param>
    /// <param name="soundPatch">The sound override patch after a synchronization, or null before the first one.</param>
    internal void SetHostState(bool hostOn, SteamUiPatchSnapshot? soundPatch)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _hostConnected = hostOn && soundPatch?.State == SteamUiPatchState.Verified;
            if (!hostOn || soundPatch is null)
            {
                _playbackStatus = null;
            }

            var status = _hostConnected
                ? PlaybackText(_playbackStatus, _overrides.Sounds.Count)
                : IntegrationText(hostOn, soundPatch);
            if (_state.Integration == status)
            {
                return;
            }

            _state = _state with { Integration = status };
        }

        Changed?.Invoke();
    }

    internal void ReportPlaybackStatus(SteamSoundOverrideStatus status)
    {
        lock (_sync)
        {
            if (_disposed || status.Revision != _overrides.Revision || status.Resources > _overrides.Sounds.Count)
            {
                return;
            }

            _playbackStatus = status;
            if (!_hostConnected)
            {
                return;
            }

            var text = PlaybackText(status, _overrides.Sounds.Count);
            if (_state.Integration == text)
            {
                return;
            }

            _state = _state with { Integration = text };
        }

        Changed?.Invoke();
    }

    private static string PlaybackText(SteamSoundOverrideStatus? status, int requested)
    {
        if (status is null)
        {
            return requested == 0
                ? "Waiting for Steam to restore stock sounds."
                : "Waiting for Steam to check the replacement audio.";
        }

        if (status.Loading)
        {
            return "Steam is checking the replacement audio. Stock sounds remain active while it loads.";
        }

        if (status.Error is { Length: > 0 } error)
        {
            return $"{status.Resources} resources accepted by Steam. {error} Rejected events use stock sounds.";
        }

        return requested == 0
            ? "Steam defaults are active."
            : $"{status.Resources} resources accepted by Steam. Unmatched events use stock sounds.";
    }

    /// <summary>The integration line for a host switch and override patch state.</summary>
    /// <param name="hostOn">Whether WSGM's own Steam surfaces are on.</param>
    /// <param name="soundPatch">The sound override patch after a synchronization, or null before the first one.</param>
    /// <returns>The line the sound page shows.</returns>
    internal static string IntegrationText(bool hostOn, SteamUiPatchSnapshot? soundPatch)
    {
        if (!hostOn)
        {
            return "Steam integration is off. The sound-pack selection is saved.";
        }

        if (soundPatch is null)
        {
            return "Waiting for Steam sound integration.";
        }

        return soundPatch.State == SteamUiPatchState.Verified
            ? "Steam sound override connected. Each replacement is checked before playback."
            : soundPatch.LastFailure ?? "Steam sound overrides are unavailable; stock sounds remain in use.";
    }

    internal SoundPackState ReadState()
    {
        lock (_sync)
        {
            return _state;
        }
    }

    internal SteamSoundOverrideState ReadOverrides()
    {
        lock (_sync)
        {
            return _overrides;
        }
    }

    internal Task<SteamUiCommandResult> RefreshAsync(CancellationToken token)
    {
        return RunAsync(() =>
        {
            Load();
            return Task.CompletedTask;
        }, token);
    }

    internal void ConfigurationChanged()
    {
        if (ReadState().Selected == _readSelected())
        {
            return;
        }

        _ = RunAsync(() =>
        {
            var selected = _readSelected();
            lock (_sync)
            {
                _state = _state with { Selected = selected };
            }

            Load();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    internal Task<SteamUiCommandResult> SelectAsync(string id, CancellationToken token)
    {
        return RunAsync(() =>
        {
            if (id.Length > 0)
            {
                _library.ReadPack(id);
            }

            _saveSelected(id);
            lock (_sync)
            {
                _state = _state with { Selected = id };
            }

            Load();
            return Task.CompletedTask;
        }, token);
    }

    internal Task<SteamUiCommandResult> DeleteAsync(string id, CancellationToken token)
    {
        return RunAsync(() =>
        {
            // The pack goes first: a delete that fails changes nothing, and the selection stays. The
            // overrides are data URIs, so nothing in Steam holds the files.
            StopPreview();
            _library.Delete(id);
            try
            {
                if (ReadState().Selected == id)
                {
                    _saveSelected("");
                    lock (_sync)
                    {
                        _state = _state with { Selected = "" };
                    }
                }
            }
            finally
            {
                // Even when config is unreadable after a successful delete, retract the removed
                // pack's cached data. Its retained saved choice is reported as unavailable.
                Load();
            }

            return Task.CompletedTask;
        }, token);
    }

    internal Task<SteamUiCommandResult> ImportAsync(string path, CancellationToken token)
    {
        return RunAsync(() =>
        {
            // The install refuses an archive whose expanded size would be a zip bomb; any other size is fine.
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new InvalidDataException("Choose an existing ZIP archive.");
            }

            using var archive = File.OpenRead(path);
            StopPreview();
            _library.Install(archive);
            Load();
            return Task.CompletedTask;
        }, token);
    }

    internal Task<SteamUiCommandResult> BrowseAsync(int page, string search, CancellationToken token)
    {
        return RunAsync(async () =>
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
            var result = await _store.QuerySoundsAsync(page, search, request.Token).ConfigureAwait(false);
            lock (_sync)
            {
                _state = _state with
                {
                    Listings = result.Items.ToArray(), Total = result.Total, Page = page
                };
            }
        }, token);
    }

    internal Task<SteamUiCommandResult> InstallAsync(string storeId, CancellationToken token)
    {
        return RunAsync(async () =>
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
            var details = await _store.GetAsync(storeId, request.Token).ConfigureAwait(false);
            if (details.Summary.ManifestVersion > 3 || details.Summary.DownloadId is not { Length: > 0 } blob)
            {
                throw new InvalidDataException("This listing has no compatible sound-pack download.");
            }

            using var archive = await _store.DownloadBlobAsync(blob, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            StopPreview();
            _library.Install(archive, storeId, details.Source);
            Load();
        }, token);
    }

    internal Task<SteamUiCommandResult> PreviewAsync(string id, string asset, CancellationToken token)
    {
        var epoch = Interlocked.Increment(ref _previewEpoch);
        return RunAsync(() =>
        {
            _library.ReadPack(id);
            var path = _library.AssetPath(id, asset);
            lock (_previewSync)
            {
                if (epoch != Interlocked.Read(ref _previewEpoch))
                {
                    return Task.CompletedTask;
                }

                _preview ??= CreatePreview();
                _preview.Play(path);
                _playingPreviewEpoch = epoch;
            }

            return Task.CompletedTask;
        }, token);
    }

    internal async Task<SteamUiCommandResult> StopPreviewAsync(CancellationToken token)
    {
        // Invalidate queued play requests immediately, before waiting for a media worker. Stop
        // never waits on package/network operations and cannot start an old preview after close.
        var epoch = Interlocked.Increment(ref _previewEpoch);
        try
        {
            await Task.Run(() => StopPreviewCore(epoch), token).ConfigureAwait(false);
            return new SteamUiCommandResult(true, null);
        }
        catch (Exception error)
        {
            _report($"Sounds preview stop: {error.Message}");
            return new SteamUiCommandResult(false, error.Message);
        }
    }

    private void StopPreview()
    {
        var epoch = Interlocked.Increment(ref _previewEpoch);
        StopPreviewCore(epoch);
    }

    private void StopPreviewCore(long epoch)
    {
        lock (_previewSync)
        {
            if (_playingPreviewEpoch <= epoch)
            {
                _preview?.Stop();
                _playingPreviewEpoch = 0;
            }
        }
    }

    internal string[] PreviewAssets(SoundPack pack)
    {
        return pack.Assets;
    }

    private AudioFilePreview CreatePreview()
    {
        var preview = new AudioFilePreview();
        preview.Failed += failure =>
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _state = _state with { Error = "Preview failed: " + failure.Message };
            }

            _report($"Sounds preview failed: {failure.Error}, HRESULT 0x{failure.HResult:X8}: {failure.Message}");
            Changed?.Invoke();
        };
        return preview;
    }

    private void Load()
    {
        // Built whole and published once. Failure to rebuild content must retract stale bytes, but
        // preview and repository failures leave the active selection alone.
        try
        {
            LoadCore();
        }
        catch
        {
            lock (_sync)
            {
                _overrides = new SteamSoundOverrideState(new Dictionary<string, IReadOnlyList<string>>(), ++_revision);
                _playbackStatus = null;
                _state = _state with
                {
                    Compatibility = "Sound content could not be refreshed. Steam defaults remain active."
                };
                if (_hostConnected)
                {
                    _state = _state with { Integration = PlaybackText(null, 0) };
                }
            }

            throw;
        }
    }

    private void LoadCore()
    {
        var resourcesPath = Path.Combine(_steamDirectory() ?? "", "steamui", "sounds");
        var resources = Directory.Exists(resourcesPath)
            ? Directory.EnumerateFiles(resourcesPath).Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.Ordinal).ToArray()
            : [];
        var packs = _library.Read().Select(item => item.Error is null
            ? item with { Compatibility = _library.InspectCompatibility(item, resources) }
            : item).ToArray();
        var selected = ReadState().Selected;
        var pack = packs.FirstOrDefault(item => item.Id == selected && item.Error is null);
        var compatibility = pack is null ? "Steam defaults." : "";
        var sounds = pack is null
            ? new Dictionary<string, string[]>()
            : _library.BuildOverrides(pack, resources, out compatibility);
        if (selected.Length > 0 && pack is null)
        {
            compatibility = "The selected pack is unavailable. Steam defaults remain active.";
        }

        if (pack is not null && resources.Length == 0)
        {
            compatibility = "Steam sound resources are unavailable. Steam defaults remain active.";
        }

        lock (_sync)
        {
            _state = _state with { Packs = packs, Compatibility = compatibility };
            _overrides = new SteamSoundOverrideState(
                sounds.ToDictionary(static sound => sound.Key, static sound => (IReadOnlyList<string>)sound.Value),
                ++_revision);
            _playbackStatus = null;
            if (_hostConnected)
            {
                _state = _state with { Integration = PlaybackText(null, sounds.Count) };
            }
        }
    }

    private async Task<SteamUiCommandResult> RunAsync(Func<Task> action, CancellationToken token)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new SteamUiCommandResult(false, "Sound packs are unavailable.");
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownToken);
        try
        {
            await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SteamUiCommandResult(false, "Cancelled.");
        }

        try
        {
            linked.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _state = _state with { Busy = true, Error = null };
            }

            Changed?.Invoke();
            await Task.Run(action, linked.Token).ConfigureAwait(false);
            return new SteamUiCommandResult(true, null);
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                _state = _state with { Error = error.Message };
            }

            _report($"Sounds: {error.Message}");
            return new SteamUiCommandResult(false, error.Message);
        }
        finally
        {
            lock (_sync)
            {
                _state = _state with { Busy = false };
            }

            _operations.Release();
            Changed?.Invoke();
        }
    }
}
