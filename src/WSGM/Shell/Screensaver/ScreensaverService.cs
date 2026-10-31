using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     One owner for both configuration surfaces: Steam's native screensaver selection, which source plays, and
///     the reconciliation of the disposable scene process with that selection.
/// </summary>
/// <remarks>
///     The sources and their scan belong to <see cref="ScreensaverCatalog" />; the scene host process belongs to
///     <see cref="ScreensaverPlayerProcess" />. Player starts and stops are queued one after another, so a second
///     player never starts before the previous one was released.
/// </remarks>
internal sealed class ScreensaverService : ISteamScreensaverContentBackend, IAsyncDisposable, IChangeSource
{
    private readonly ScreensaverCatalog _catalog;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ScreensaverPlayerProcess _player;
    private readonly Lock _state = new();
    private readonly Func<bool> _steamAvailable;
    private readonly Lock _workGate = new();
    private Task _catalogWork = Task.CompletedTask;
    private string _desired = "";
    private long _dismissRevision;
    private Task? _disposal;
    private volatile bool _disposed;
    private string? _error;
    private string? _failedForActivation;
    private long _generation;
    private bool _nativeActive;
    private string _nativeSelection = "";
    private bool _previewSelection;
    private long _revision;
    private string _selected = "";
    private string? _selectionRequest;
    private long _selectionRevision;
    private CancellationTokenSource? _starting;
    private string _status = "Local image folders and sandboxed scene packs. Files are read in place.";
    private Task _work = Task.CompletedTask;

    /// <summary>Creates the owner; the first state read scans the sources.</summary>
    /// <param name="context">The user data context the scene host's profile lives in.</param>
    /// <param name="configuration">The current screensaver configuration.</param>
    /// <param name="persist">Saves a change to the screensaver configuration.</param>
    /// <param name="steamAvailable">Whether Steam Big Picture with WSGM's integration is reachable for a selection.</param>
    internal ScreensaverService(UserDataContext context, Func<ScreensaverConfig> configuration,
        Action<Action<ScreensaverConfig>> persist, Func<bool> steamAvailable)
    {
        _steamAvailable = steamAvailable;
        _catalog = new ScreensaverCatalog(configuration, persist, _lifetime.Token);
        _player = new ScreensaverPlayerProcess(context, _lifetime.Token);
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Takes the reloaded configuration and rescans the sources in the background.</summary>
    internal void ConfigurationChanged()
    {
        _catalog.MarkDirty();
        lock (_workGate)
        {
            if (!_disposed)
            {
                _catalogWork = Task.Run(RefreshCatalog);
                Log.Observe(_catalogWork, "Refreshing screensaver configuration");
            }
        }
    }

    /// <summary>Rescans the sources when the scan is stale, then answers the state both surfaces show.</summary>
    /// <returns>The state.</returns>
    internal SteamScreensaverContentState ReadState()
    {
        string? active;
        lock (_state)
        {
            active = _nativeActive ? _selected : null;
        }

        if (_catalog.RefreshDue(active))
        {
            RefreshCatalog();
        }

        return Snapshot();
    }

    /// <summary>The state both surfaces show, without rescanning.</summary>
    /// <returns>The state.</returns>
    internal SteamScreensaverContentState Snapshot()
    {
        lock (_state)
        {
            return new SteamScreensaverContentState
            {
                Sources = _catalog.Entries.Select(entry => Project(entry.Source, entry.Content)).ToArray(),
                SelectedId = _selected,
                NativeSelection = _nativeSelection,
                SelectionRequest = _selectionRequest,
                SelectionRevision = _selectionRevision,
                PreviewSelection = _previewSelection,
                DismissRevision = _dismissRevision,
                Status = _selectionRequest is not null ? "Applying screensaver selection…"
                    : !_nativeActive ? "Steam controls idle timing."
                    : _selected.Length == 0 || _failedForActivation == _selected
                        ? "Steam's stock screensaver is active."
                        : _status,
                Error = _error,
                Revision = _revision
            };
        }
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> ExecuteAsync(SteamScreensaverContentCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
        {
            return new SteamUiCommandResult(false, "The screensaver session has closed.");
        }

        try
        {
            switch (command.Action)
            {
                case "thumbnail":
                    var thumbnail = await ReadThumbnailAsync(command.Id, cancellationToken).ConfigureAwait(false);
                    var image = thumbnail is null ? null : ImageHeader.DataUrl(thumbnail);
                    return new SteamUiCommandResult(true, null,
                        JsonSerializer.SerializeToElement(image, ScreensaverJson.Default.String));
                case "rename":
                    Rename(command.Id, command.Name);
                    break;
                case "relocate":
                    _catalog.Relocate(command.Id, command.Folder);
                    break;
                case "addImages":
                case "addPack":
                    _catalog.Add(command.Name, command.Folder, command.Action == "addPack");
                    break;
                case "configure":
                    _catalog.Configure(command);
                    break;
                case "remove":
                    _catalog.Remove(command.Id);
                    lock (_state)
                    {
                        if (_selected == command.Id)
                        {
                            _selectionRequest = "steam-gameslideshow";
                            _selectionRevision++;
                            _previewSelection = false;
                        }
                    }

                    break;
                case "select":
                case "preview":
                    return Select(command.Id, command.Action == "preview");
                case "selectionResult":
                    lock (_state)
                    {
                        if (command.Revision != _selectionRevision)
                        {
                            return SteamUiCommandResult.Applied;
                        }

                        _selectionRequest = null;
                        _error = command.Error;
                        _previewSelection = false;
                    }

                    Publish();
                    return SteamUiCommandResult.Applied;
                case "playback":
                    return ObservePlayback(command);
                case "refresh":
                    lock (_state)
                    {
                        _failedForActivation = null;
                    }

                    break;
                default:
                    return new SteamUiCommandResult(false, "Unknown screensaver operation.");
            }

            _catalog.MarkDirty();
            lock (_state)
            {
                if (_failedForActivation is null)
                {
                    _error = null;
                }
            }

            await Task.Run(RefreshCatalog, cancellationToken).ConfigureAwait(false);
            Publish();
            return SteamUiCommandResult.Applied;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                                      or InvalidOperationException)
        {
            SetStatus("Steam's stock screensaver remains available.", error.Message);
            return new SteamUiCommandResult(false, error.Message);
        }
    }

    /// <summary>Steam stopped or changed: the custom player stops and a pending selection is abandoned.</summary>
    /// <returns>A task that completes once the player has been released.</returns>
    internal Task StopForSteamAsync()
    {
        lock (_state)
        {
            _nativeActive = false;
            _desired = "";
            _failedForActivation = null;
            if (_selectionRequest is not null)
            {
                _selectionRequest = null;
                _previewSelection = false;
                _error = "Steam changed before the selection was confirmed. Select it again when Steam is ready.";
            }
        }

        return QueuePlayer(null);
    }

    /// <summary>A registered source's cover; see <see cref="ScreensaverCatalog.ReadThumbnailAsync" />.</summary>
    /// <param name="id">The source.</param>
    /// <param name="cancellationToken">Cancels waiting and reading, as the session's end does.</param>
    /// <returns>The JPEG bytes, or null once the session has closed or no cover can be read.</returns>
    internal async Task<byte[]?> ReadThumbnailAsync(string id, CancellationToken cancellationToken)
    {
        CancellationTokenSource lifetime;
        lock (_workGate)
        {
            if (_disposed)
            {
                return null;
            }

            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }

        using (lifetime)
        {
            return await _catalog.ReadThumbnailAsync(id, lifetime.Token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_workGate)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        try
        {
            await StopForSteamAsync().ConfigureAwait(false);
            await _catalogWork.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _catalog.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }

    private void Rename(string id, string name)
    {
        var saved = _catalog.Rename(id, name);
        lock (_state)
        {
            if (_selected == id)
            {
                _selectionRequest = ScreensaverContent.SelectorId(saved);
                _selectionRevision++;
                _previewSelection = false;
            }
        }
    }

    private SteamUiCommandResult Select(string id, bool preview)
    {
        if (!_steamAvailable())
        {
            return new SteamUiCommandResult(false, "Start Steam Big Picture with WSGM Steam integration enabled to select a screensaver.");
        }

        lock (_state)
        {
            var source = _catalog.Entries.FirstOrDefault(entry => entry.Source.Id == id)?.Source;
            if (source is null && id is not "steam-gameslideshow" and not "steam-bouncinglogo")
            {
                return new SteamUiCommandResult(false, "This source was removed.");
            }

            _selectionRequest = source is null ? id : ScreensaverContent.SelectorId(source.Name);
            _selectionRevision++;
            _previewSelection = preview;
            _error = null;
        }

        Publish();
        return SteamUiCommandResult.Applied;
    }

    private SteamUiCommandResult ObservePlayback(SteamScreensaverContentCommand command)
    {
        lock (_state)
        {
            var entry = _catalog.Entries.FirstOrDefault(item => item.Source.Id == command.Id
                && ScreensaverContent.SelectorId(item.Source.Name) == command.SelectorId);
            if (command.Id.Length > 0 && entry is null)
            {
                return new SteamUiCommandResult(false, "The native screensaver source no longer matches the registered source.");
            }

            _selected = entry?.Source.Id ?? "";
            _nativeSelection = command.SelectorId;
            _nativeActive = command.Active;
            if (!command.Active)
            {
                _failedForActivation = null;
            }

            if (_selectionRequest == command.SelectorId)
            {
                _selectionRequest = null;
            }
        }

        ReconcilePlayer();
        Publish();
        return SteamUiCommandResult.Applied;
    }

    private void RefreshCatalog()
    {
        if (_disposed || !_catalog.TryRefresh(out var previous, out var next))
        {
            return;
        }

        lock (_state)
        {
            // A source that failed to play gets another chance once its content changed.
            var previousFailure = previous.FirstOrDefault(entry => entry.Source.Id == _failedForActivation);
            var replacement = next.FirstOrDefault(entry => entry.Source.Id == _failedForActivation);
            if (replacement?.Content.Playback is { } repaired
                && repaired.Revision != previousFailure?.Content.Playback?.Revision)
            {
                _failedForActivation = null;
            }
        }

        ReconcilePlayer();
        Publish();
    }

    private void ReconcilePlayer()
    {
        ScreensaverEntry? entry;
        lock (_state)
        {
            entry = _nativeActive && !_disposed && _failedForActivation != _selected
                ? _catalog.Entries.FirstOrDefault(candidate => candidate.Source.Id == _selected && candidate.Content.Available)
                : null;
            var signature = entry?.Content.Playback is { } content
                ? $"{entry.Source.Id}|{content.Revision}|{content.Shuffle}|{content.IntervalSeconds}|{content.Fill}|{content.FramesPerSecond}"
                : "";
            if (signature == _desired)
            {
                return;
            }

            _desired = signature;
        }

        _ = QueuePlayer(entry);
    }

    private Task QueuePlayer(ScreensaverEntry? entry)
    {
        lock (_workGate)
        {
            _starting?.Cancel();
            var previous = _work;
            var generation = ++_generation;
            _work = ReconcileAsync(previous, generation, entry);
            return _work;
        }
    }

    private bool IsCurrent(long generation)
    {
        return generation == Volatile.Read(ref _generation) && !_disposed;
    }

    private async Task ReconcileAsync(Task previous, long generation, ScreensaverEntry? entry)
    {
        await Task.Yield();
        try
        {
            await previous.ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            if (!await _player.StopAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The previous screensaver process could not be released. A second player was not started.");
            }

            if (entry?.Content.Playback is not { } playback || !IsCurrent(generation))
            {
                if (IsCurrent(generation))
                {
                    lock (_state)
                    {
                        if (_error is null)
                        {
                            _status = _nativeActive && _selected.Length > 0
                                ? "Local content is unavailable. Steam's stock screensaver is active."
                                : "Custom player stopped. Steam controls idle timing.";
                        }
                    }

                    Publish();
                }

                return;
            }

            using var start = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            start.CancelAfter(TimeSpan.FromSeconds(25));
            lock (_workGate)
            {
                _starting = start;
            }

            try
            {
                var sourceId = entry.Source.Id;
                var playing = await _player.StartAsync(playback, () => IsCurrent(generation),
                    () => SetStatus("Custom screensaver active.", null),
                    (error, dismissed) => PlayerEnded(sourceId, error, dismissed), start.Token).ConfigureAwait(false);
                SetStatus("Starting custom screensaver…", null);
                await playing.WaitAsync(start.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_workGate)
                {
                    if (ReferenceEquals(_starting, start))
                    {
                        _starting = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            await _player.StopAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await _player.StopAsync().ConfigureAwait(false);
            if (IsCurrent(generation))
            {
                lock (_state)
                {
                    _failedForActivation = entry?.Source.Id;
                    _desired = "";
                }

                SetStatus("Steam's stock screensaver is the fallback.", error.Message);
            }
        }
    }

    private void PlayerEnded(string sourceId, string? error, bool dismissed)
    {
        lock (_state)
        {
            _failedForActivation = sourceId;
            _desired = "";
            if (dismissed)
            {
                _dismissRevision++;
            }
        }

        SetStatus("Steam's stock screensaver is the fallback.", error);
        _ = QueuePlayer(null);
    }

    private void SetStatus(string status, string? error)
    {
        lock (_state)
        {
            _status = status;
            _error = error;
        }

        if (error is not null)
        {
            Log.Change("screensaver.player", "Screensaver fallback: " + error);
        }

        Publish();
    }

    private void Publish()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    private static SteamScreensaverSource Project(ScreensaverSourceConfig source, ScreensaverContentResult content)
    {
        return new SteamScreensaverSource
        {
            Id = source.Id, SelectorId = ScreensaverContent.SelectorId(source.Name), Name = source.Name,
            Type = content.Type, Folder = source.Folder, Available = content.Available, Detail = content.Detail,
            Pack = source.Pack, ImageCount = content.Playback?.Images.Count ?? 0,
            HasPreview = content.Preview is not null, PreviewRevision = content.PreviewRevision,
            Shuffle = source.Shuffle, IntervalSeconds = source.IntervalSeconds, Fill = source.Fill,
            FramesPerSecond = source.FramesPerSecond,
            Kind = content.Type == "scene" ? "Scene pack" : source.Pack ? "Image pack" : "Image folder",
            Summary = Summary(source, content)
        };
    }

    /// <summary>What a source plays, in one line both the Steam page and the overlay show.</summary>
    /// <param name="source">The configured source.</param>
    /// <param name="content">What discovery found in it.</param>
    /// <returns>The frame rate of a scene, or the image count, order and interval of a slideshow.</returns>
    private static string Summary(ScreensaverSourceConfig source, ScreensaverContentResult content)
    {
        if (content.Type == "scene")
        {
            return string.Create(CultureInfo.InvariantCulture, $"{source.FramesPerSecond} fps animation");
        }

        var images = content.Playback?.Images.Count ?? 0;
        var seconds = source.IntervalSeconds;
        var interval = seconds < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds} seconds")
            : seconds < 3600
                ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60d} minutes")
                : "1 hour";
        return string.Create(CultureInfo.InvariantCulture,
            $"{images} {(images == 1 ? "image" : "images")} · {(source.Shuffle ? "Shuffle" : "Filename order")} · {interval}");
    }
}
