using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Shell;

/// <summary>One owner for local source discovery, both configuration surfaces and the disposable scene process.</summary>
internal sealed class ScreensaverService : ISteamScreensaverContentBackend, IAsyncDisposable, IChangeSource
{
    private readonly UserDataContext _context;
    private readonly Func<ScreensaverConfig> _configuration;
    private readonly Action<Action<ScreensaverConfig>> _persist;
    private readonly Func<bool> _steamAvailable;
    private readonly Lock _state = new();
    private readonly Lock _workGate = new();
    private readonly SemaphoreSlim _scan = new(1, 1);
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly Dictionary<string, (string Revision, byte[] Bytes)> _thumbnails = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Entry[] _entries = [];
    private Task _work = Task.CompletedTask;
    private Task _reader = Task.CompletedTask;
    private Task _catalogWork = Task.CompletedTask;
    private Task? _disposal;
    private CancellationTokenSource? _starting;
    private CancellationTokenSource? _reading;
    private NamedPipeServerStream? _pipe;
    private Process? _helper;
    private WorkerJobObject? _job;
    private bool _contained;
    private volatile bool _disposed;
    private bool _nativeActive;
    private string _selected = "";
    private string _nativeSelection = "";
    private string _desired = "";
    private string? _failedForActivation;
    private string? _selectionRequest;
    private long _selectionRevision;
    private bool _previewSelection;
    private long _dismissRevision;
    private long _revision;
    private long _generation;
    private long _lastMissingScan;
    private int _dirty = 1;
    private string _status = "Local image folders and sandboxed scene packs. Files are read in place.";
    private string? _error;

    internal ScreensaverService(UserDataContext context, Func<ScreensaverConfig> configuration,
        Action<Action<ScreensaverConfig>> persist, Func<bool> steamAvailable)
    {
        _context = context;
        _configuration = configuration;
        _persist = persist;
        _steamAvailable = steamAvailable;
    }

    public event Action? Changed;

    internal void ConfigurationChanged()
    {
        Interlocked.Exchange(ref _dirty, 1);
        lock (_workGate)
        {
            if (!_disposed)
            {
                _catalogWork = Task.Run(RefreshCatalog);
                Log.Observe(_catalogWork, "Refreshing screensaver configuration");
            }
        }
    }

    internal SteamScreensaverContentState ReadState()
    {
        if (Volatile.Read(ref _dirty) != 0 || ActiveFolderMissing() || MissingSourceDue())
        {
            RefreshCatalog();
        }

        return Snapshot();
    }

    internal SteamScreensaverContentState Snapshot()
    {
        lock (_state)
        {
            return new SteamScreensaverContentState
            {
                Sources = _entries.Select(entry => Project(entry.Source, entry.Content)).ToArray(),
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
                    var replacement = ValidateFolder(command.Folder);
                    _persist(config =>
                    {
                        var source = config.Sources.FirstOrDefault(source => source.Id == command.Id)
                                     ?? throw new InvalidOperationException("This source was removed.");
                        source.Folder = replacement;
                    });
                    break;
                case "addImages":
                case "addPack":
                    Add(command.Name, command.Folder, command.Action == "addPack");
                    break;
                case "configure":
                    _persist(config =>
                    {
                        var source = config.Sources.FirstOrDefault(source => source.Id == command.Id)
                                     ?? throw new InvalidOperationException("This source was removed.");
                        switch (command.Option)
                        {
                            case "shuffle":
                                source.Shuffle = command.Shuffle;
                                break;
                            case "fill":
                                source.Fill = command.Fill;
                                break;
                            case "intervalSeconds":
                                source.IntervalSeconds = Math.Clamp(command.IntervalSeconds, 2, 3600);
                                break;
                            case "framesPerSecond":
                                source.FramesPerSecond = Math.Clamp(command.FramesPerSecond, 1, 60);
                                break;
                            default:
                                throw new ArgumentException("Unknown source option.");
                        }
                    });
                    break;
                case "remove":
                    _persist(config => config.Sources.RemoveAll(source => source.Id == command.Id));
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

            Interlocked.Exchange(ref _dirty, 1);
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
            await _thumbnailGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                Entry? entry;
                HashSet<string> currentIds;
                lock (_state)
                {
                    entry = _entries.FirstOrDefault(item => item.Source.Id == id);
                    currentIds = _entries.Select(item => item.Source.Id).ToHashSet(StringComparer.Ordinal);
                }

                foreach (var removed in _thumbnails.Keys.Where(key => !currentIds.Contains(key)).ToArray())
                {
                    _thumbnails.Remove(removed);
                }

                if (entry?.Content.Preview is not { } preview)
                {
                    return null;
                }

                if (_thumbnails.TryGetValue(id, out var cached) && cached.Revision == entry.Content.PreviewRevision)
                {
                    return cached.Bytes;
                }

                if (!entry.Content.Available)
                {
                    return null;
                }

                var relative = Path.GetRelativePath(entry.Source.Folder, preview);
                var path = ScreensaverContent.AssetPath(entry.Source.Folder, relative);
                var bytes = await Task.Run(() => ScreensaverThumbnail.Read(path), lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                if (bytes is not null)
                {
                    _thumbnails[id] = (entry.Content.PreviewRevision, bytes);
                }

                return bytes;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
    }

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
            await _scan.WaitAsync().ConfigureAwait(false);
            try
            {
                DisposeWatchers();
            }
            finally
            {
                _scan.Release();
                await _thumbnailGate.WaitAsync().ConfigureAwait(false);
                _thumbnails.Clear();
                _thumbnailGate.Release();
                _lifetime.Dispose();
            }
        }
    }

    private bool MissingSourceDue()
    {
        lock (_state)
        {
            var interval = _entries.Any(entry => !entry.Content.Available) ? 10 : 60;
            return _entries.Length > 0
                   && Stopwatch.GetElapsedTime(_lastMissingScan) >= TimeSpan.FromSeconds(interval);
        }
    }

    private bool ActiveFolderMissing()
    {
        string? folder;
        lock (_state)
        {
            folder = _nativeActive
                ? _entries.FirstOrDefault(entry => entry.Source.Id == _selected && entry.Content.Available)?.Source.Folder
                : null;
        }

        // Moving or unplugging the watched root need not produce an event inside that root.
        // Check the one active source on the existing host publication, without another poller.
        return folder is not null && !Directory.Exists(folder);
    }

    private void Add(string name, string folder, bool pack)
    {
        name = name.Trim();
        ValidateName(name);
        folder = ValidateFolder(folder);

        _persist(config =>
        {
            if (config.Sources.Any(source => string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A source already has that name. Choose a different name.");
            }

            config.Sources.Add(new ScreensaverSourceConfig
            {
                Id = Guid.NewGuid().ToString("N"), Name = name, Folder = folder, Pack = pack
            });
        });
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Choose a name from 1 to 128 characters.");
        }
    }

    private static string ValidateFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Choose an absolute local folder path.");
        }

        var root = Path.GetPathRoot(folder);
        if (root is null || new DriveInfo(root).DriveType == DriveType.Network)
        {
            throw new ArgumentException("Screensaver sources must be on a local or removable drive.");
        }

        return Path.GetFullPath(folder);
    }

    private void Rename(string id, string name)
    {
        name = name.Trim();
        ValidateName(name);
        _persist(config =>
        {
            var source = config.Sources.FirstOrDefault(source => source.Id == id)
                         ?? throw new InvalidOperationException("This source was removed.");
            if (config.Sources.Any(other => other.Id != id
                && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A source already has that name. Choose a different name.");
            }

            source.Name = name;
        });
        lock (_state)
        {
            if (_selected == id)
            {
                _selectionRequest = ScreensaverContent.SelectorId(name);
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
            var source = _entries.FirstOrDefault(entry => entry.Source.Id == id)?.Source;
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
        Entry? entry;
        lock (_state)
        {
            entry = _entries.FirstOrDefault(item => item.Source.Id == command.Id
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
        if (_disposed || !_scan.Wait(0))
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _dirty, 0);
            var version = typeof(AppConfig).Assembly.GetName().Version ?? new Version(2, 3, 2);
            var sources = _configuration().Sources.Select(Clone).ToArray();
            var next = sources.Select(source =>
            {
                var content = ScreensaverContent.Read(source, version);
                var previous = _entries.FirstOrDefault(entry => entry.Source.Id == source.Id && entry.Source.Folder == source.Folder);
                if (!content.Available && previous is not null)
                {
                    content = content with { Preview = previous.Content.Preview, PreviewRevision = previous.Content.PreviewRevision };
                }

                return new Entry(source, content);
            }).ToArray();
            if (_disposed)
            {
                return;
            }

            DisposeWatchers();
            foreach (var entry in next)
            {
                if (!entry.Content.Available)
                {
                    Log.Change("screensaver.source." + entry.Source.Id,
                        $"Screensaver '{entry.Source.Name}' unavailable: {entry.Content.Detail}");
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(entry.Source.Folder)
                    {
                        IncludeSubdirectories = entry.Source.Pack,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                    };
                    watcher.Created += SourceChanged;
                    watcher.Changed += SourceChanged;
                    watcher.Deleted += SourceChanged;
                    watcher.Renamed += SourceChanged;
                    watcher.Error += WatcherFailed;
                    _watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    Log.Change("screensaver.watch." + entry.Source.Id, "Screensaver folder watch unavailable: " + error.Message);
                }
            }

            lock (_state)
            {
                var previousFailure = _entries.FirstOrDefault(entry => entry.Source.Id == _failedForActivation);
                var replacement = next.FirstOrDefault(entry => entry.Source.Id == _failedForActivation);
                if (replacement?.Content.Playback is { } repaired
                    && repaired.Revision != previousFailure?.Content.Playback?.Revision)
                {
                    _failedForActivation = null;
                }

                _entries = next;
                _lastMissingScan = Stopwatch.GetTimestamp();
            }

            ReconcilePlayer();
            Publish();
        }
        finally
        {
            _scan.Release();
        }
    }

    private void SourceChanged(object sender, FileSystemEventArgs arguments)
    {
        Interlocked.Exchange(ref _dirty, 1);
    }

    private void WatcherFailed(object sender, ErrorEventArgs arguments)
    {
        Interlocked.Exchange(ref _dirty, 1);
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void ReconcilePlayer()
    {
        Entry? entry;
        string signature;
        lock (_state)
        {
            entry = _nativeActive && !_disposed && _failedForActivation != _selected
                ? _entries.FirstOrDefault(candidate => candidate.Source.Id == _selected && candidate.Content.Available)
                : null;
            signature = entry?.Content.Playback is { } content
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

    private Task QueuePlayer(Entry? entry)
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

    private async Task ReconcileAsync(Task previous, long generation, Entry? entry)
    {
        await Task.Yield();
        try
        {
            await previous.ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            if (!await StopPlayerAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The previous screensaver process could not be released. A second player was not started.");
            }

            if (entry?.Content.Playback is not { } playback || _disposed || generation != Volatile.Read(ref _generation))
            {
                if (!_disposed && generation == Volatile.Read(ref _generation))
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
                await StartPlayerAsync(entry.Source.Id, playback, generation, start.Token).ConfigureAwait(false);
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
            await StopPlayerAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await StopPlayerAsync().ConfigureAwait(false);
            if (generation == Volatile.Read(ref _generation) && !_disposed)
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

    private async Task StartPlayerAsync(string sourceId, ScreensaverPlayback playback, long generation, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Screensaver", "WSGM.ScreensaverHost.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The screensaver host is missing. Repair or update WSGM.");
        }

        _job = WorkerJobObject.Create();
        var pipeName = "WSGM.Screensaver." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current Windows account is unavailable.");
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;" + sid + ")");
        _pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, ScreensaverWire.MaximumPacketBytes, ScreensaverWire.MaximumPacketBytes,
            security, HandleInheritability.None, PipeAccessRights.TakeOwnership);
        BrowserNative.SetMediumPipeLabel(_pipe.SafePipeHandle);
        using var parent = Process.GetCurrentProcess();
        var arguments = LaunchArguments.Join(new[]
        {
            pipeName, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Path.Combine(_context.Root, "ScreensaverProfile")
        });
        var connection = _pipe.WaitForConnectionAsync(token);
        if (ElevationCheck.IsCurrentProcessElevated() != false)
        {
            var launched = await UnelevatedLauncher.TryStartViaScheduledTaskAsync(_context, executable, arguments,
                DateTimeOffset.UtcNow.AddSeconds(20), Path.GetDirectoryName(executable), token).ConfigureAwait(false);
            if (launched == ScheduledTaskLaunchDisposition.NotDispatched)
            {
                throw new InvalidOperationException("Windows could not start the screensaver at medium integrity.");
            }
        }
        else
        {
            _helper = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
            }) ?? throw new InvalidOperationException("The screensaver host did not start.");
        }

        await connection.ConfigureAwait(false);
        var hello = await ScreensaverWire.ReadAsync(_pipe, token).ConfigureAwait(false);
        if (hello.Kind != "hello" || !BrowserNative.GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId)
            || clientId != hello.ProcessId || !BrowserNative.IsMediumIntegrity(hello.ProcessId))
        {
            throw new InvalidDataException("Screensaver host identity or medium integrity could not be verified.");
        }

        _helper ??= Process.GetProcessById(hello.ProcessId);
        if (_helper.Id != hello.ProcessId
            || !string.Equals(_helper.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The pipe is not connected to the installed screensaver host.");
        }

        _job.Assign(_helper);
        _contained = true;
        _reading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reader = ReadPlayerAsync(_pipe, sourceId, generation, playing, _reading.Token);
        playback.WindowHandle = Steam.FindBigPictureWindow();
        await ScreensaverWire.WriteAsync(_pipe, playback, token).ConfigureAwait(false);
        SetStatus("Starting custom screensaver…", null);
        await playing.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task ReadPlayerAsync(Stream pipe, string sourceId, long generation,
        TaskCompletionSource playing, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await ScreensaverWire.ReadAsync(pipe, token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _generation) || _disposed)
                {
                    return;
                }

                if (message.Kind == "playing")
                {
                    playing.TrySetResult();
                    SetStatus("Custom screensaver active.", null);
                    continue;
                }

                if (message.Kind is "closed" or "error")
                {
                    playing.TrySetException(new IOException(message.Error ?? "The screensaver player closed."));
                    lock (_state)
                    {
                        _failedForActivation = sourceId;
                        _desired = "";
                        if (message.Error == "input")
                        {
                            _dismissRevision++;
                        }
                    }

                    SetStatus("Steam's stock screensaver is the fallback.", message.Kind == "error" ? message.Error : null);
                    _ = QueuePlayer(null);
                    return;
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            playing.TrySetException(error);
            if (!token.IsCancellationRequested && generation == Volatile.Read(ref _generation) && !_disposed)
            {
                lock (_state)
                {
                    _failedForActivation = sourceId;
                    _desired = "";
                }

                SetStatus("Steam's stock screensaver is the fallback.", "The custom player exited: " + error.Message);
                _ = QueuePlayer(null);
            }
        }
    }

    private async Task<bool> StopPlayerAsync()
    {
        try
        {
            if (_pipe?.IsConnected == true && _helper is { HasExited: false })
            {
                using var close = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ScreensaverWire.WriteAsync(_pipe, new ScreensaverPlayback { Kind = "close" }, close.Token)
                    .ConfigureAwait(false);
                try
                {
                    await _helper.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
        }

        if (!_contained && _helper is { HasExited: false })
        {
            _helper.Kill(entireProcessTree: true);
            await _helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        if (_job is not null && !await _job.TerminateAndWaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            return false;
        }

        _reading?.Cancel();
        _pipe?.Dispose();
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }

        _reading?.Dispose();
        _reading = null;
        _pipe = null;
        _helper?.Dispose();
        _helper = null;
        _job?.Dispose();
        _job = null;
        _contained = false;
        return true;
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
            FramesPerSecond = source.FramesPerSecond
        };
    }

    private static ScreensaverSourceConfig Clone(ScreensaverSourceConfig source)
    {
        return new ScreensaverSourceConfig
        {
            Id = source.Id, Name = source.Name, Folder = source.Folder, Pack = source.Pack,
            Shuffle = source.Shuffle, IntervalSeconds = source.IntervalSeconds, Fill = source.Fill,
            FramesPerSecond = source.FramesPerSecond
        };
    }

    private sealed record Entry(ScreensaverSourceConfig Source, ScreensaverContentResult Content);
}
