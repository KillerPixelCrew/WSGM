using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Func<string> _readSelected;
    private readonly Action<string> _saveSelected;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Action<string> _report;
    private readonly Func<string?> _steamDirectory;
    private readonly ThemeStoreClient _store = new();
    private readonly object _sync = new();
    private bool _disposed;
    private SteamSoundOverrideState _overrides = new(new Dictionary<string, string[]>());
    private AudioFilePreview? _preview;
    private long _revision;
    private SoundPackState _state = new([], "", false, null, "Not checked yet.", [], 0, 0);

    internal SoundPackService(SoundPackLibrary library, Func<string> readSelected, Action<string> saveSelected,
        Func<string?> steamDirectory, Action<string>? report = null)
    {
        _library = library;
        _readSelected = readSelected;
        _saveSelected = saveSelected;
        _steamDirectory = steamDirectory;
        _shutdownToken = _shutdown.Token;
        _report = report ?? Log.Warn;
        _state = _state with { Selected = readSelected() };
    }

    internal long Revision => Interlocked.Read(ref _revision);

    internal void SetIntegrationStatus(string status)
    {
        lock (_sync)
        {
            if (_disposed || _state.Integration == status)
            {
                return;
            }
            _state = _state with { Integration = status };
        }
        Changed?.Invoke();
    }

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
            _preview?.Dispose();
        }
        finally
        {
            _operations.Release();
            _shutdown.Dispose();
        }
    }

    public event Action? Changed;

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
        var selected = _readSelected();
        if (ReadState().Selected == selected)
        {
            return;
        }
        _ = RunAsync(() =>
        {
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
            if (ReadState().Selected == id)
            {
                _saveSelected("");
                lock (_sync)
                {
                    _state = _state with { Selected = "" };
                    _overrides = new SteamSoundOverrideState(new Dictionary<string, string[]>(), ++_revision);
                }

                Changed?.Invoke();
            }

            _preview?.Stop();
            _library.Delete(id);
            Load();
            return Task.CompletedTask;
        }, token);
    }

    internal Task<SteamUiCommandResult> ImportAsync(string path, CancellationToken token)
    {
        return RunAsync(() =>
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Choose an existing ZIP archive smaller than 64 MB.");
            }

            using var archive = File.OpenRead(path);
            _preview?.Stop();
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
            _preview?.Stop();
            _library.Install(archive, storeId);
            Load();
        }, token);
    }

    internal Task<SteamUiCommandResult> PreviewAsync(string id, string asset, CancellationToken token)
    {
        return RunAsync(() =>
        {
            _library.ReadPack(id);
            _preview ??= CreatePreview();
            _preview.Play(_library.AssetPath(id, asset));
            return Task.CompletedTask;
        }, token);
    }

    internal Task<SteamUiCommandResult> StopPreviewAsync(CancellationToken token)
    {
        return RunAsync(() =>
        {
            _preview?.Stop();
            return Task.CompletedTask;
        }, token);
    }

    internal string[] PreviewAssets(SoundPack pack)
    {
        return pack.Assets;
    }

    private AudioFilePreview CreatePreview()
    {
        var preview = new AudioFilePreview();
        preview.Failed += error =>
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _state = _state with { Error = "Preview failed: " + error };
            }

            Changed?.Invoke();
        };
        return preview;
    }

    private void Load()
    {
        // Failure to rebuild content must retract stale bytes, but preview and repository failures
        // leave the active selection alone.
        lock (_sync)
        {
            _overrides = new SteamSoundOverrideState(new Dictionary<string, string[]>(), ++_revision);
        }
        var packs = _library.Read();
        var selected = ReadState().Selected;
        var pack = packs.FirstOrDefault(item => item.Id == selected && item.Error is null);
        var resourcesPath = Path.Combine(_steamDirectory() ?? "", "steamui", "sounds");
        var resources = Directory.Exists(resourcesPath)
            ? Directory.EnumerateFiles(resourcesPath).Select(Path.GetFileName).OfType<string>().ToArray()
            : [];
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
            _overrides = new SteamSoundOverrideState(sounds, ++_revision);
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
