using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The registered screensaver sources: their configuration edits, the scan of each folder, the folder
///     watchers that mark the scan stale, and the cover thumbnails read from the scanned content.
/// </summary>
/// <remarks>
///     Nothing polls. A watcher event, a configuration change or a command marks the catalog dirty, and the
///     next state read rescans. A source that is missing is rescanned at most every ten seconds, any other
///     every minute, so a reconnected drive reappears without a watcher inside its root.
/// </remarks>
internal sealed class ScreensaverCatalog : IAsyncDisposable
{
    private readonly Func<ScreensaverConfig> _configuration;
    private readonly CancellationToken _lifetime;
    private readonly Action<Action<ScreensaverConfig>> _persist;
    private readonly SemaphoreSlim _scan = new(1, 1);
    private readonly Lock _state = new();
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly Dictionary<string, (string Revision, byte[] Bytes)> _thumbnails = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private int _dirty = 1;
    private ScreensaverEntry[] _entries = [];
    private long _lastMissingScan;

    /// <summary>Creates the catalog; the first state read scans it.</summary>
    /// <param name="configuration">The current screensaver configuration.</param>
    /// <param name="persist">Saves a change to the screensaver configuration.</param>
    /// <param name="lifetime">The owning session's lifetime; once cancelled, no scan starts or publishes.</param>
    internal ScreensaverCatalog(Func<ScreensaverConfig> configuration, Action<Action<ScreensaverConfig>> persist,
        CancellationToken lifetime)
    {
        _configuration = configuration;
        _persist = persist;
        _lifetime = lifetime;
    }

    /// <summary>The sources as the last scan found them, in configuration order.</summary>
    internal ScreensaverEntry[] Entries
    {
        get
        {
            lock (_state)
            {
                return _entries;
            }
        }
    }

    /// <summary>Waits for a running scan, then releases the watchers and the cached covers.</summary>
    /// <returns>A task that completes once the catalog holds nothing.</returns>
    public async ValueTask DisposeAsync()
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
        }
    }

    /// <summary>Marks the scan stale, so the next state read rescans.</summary>
    internal void MarkDirty()
    {
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Whether a state read should rescan first.</summary>
    /// <param name="activeSourceId">The source Steam is playing, or null while no custom source is active.</param>
    /// <returns>True when the scan is stale, the active folder disappeared, or a periodic missing-source scan is due.</returns>
    internal bool RefreshDue(string? activeSourceId)
    {
        return Volatile.Read(ref _dirty) != 0 || ActiveFolderMissing(activeSourceId) || MissingSourceDue();
    }

    /// <summary>Scans every configured source and watches each available folder.</summary>
    /// <param name="previous">The entries this scan replaced.</param>
    /// <param name="next">The entries this scan published.</param>
    /// <returns>False when another scan is running or the session has closed; nothing was replaced.</returns>
    internal bool TryRefresh(out ScreensaverEntry[] previous, out ScreensaverEntry[] next)
    {
        previous = [];
        next = [];
        if (_lifetime.IsCancellationRequested || !_scan.Wait(0))
        {
            return false;
        }

        try
        {
            Interlocked.Exchange(ref _dirty, 0);
            var version = typeof(AppConfig).Assembly.GetName().Version ?? new Version(2, 3, 2);
            var sources = _configuration().Sources.Select(Clone).ToArray();
            var current = Entries;
            var scanned = sources.Select(source =>
            {
                var content = ScreensaverContent.Read(source, version);
                var known = current.FirstOrDefault(entry => entry.Source.Id == source.Id && entry.Source.Folder == source.Folder);
                if (!content.Available && known is not null)
                {
                    content = content with { Preview = known.Content.Preview, PreviewRevision = known.Content.PreviewRevision };
                }

                return new ScreensaverEntry(source, content);
            }).ToArray();
            if (_lifetime.IsCancellationRequested)
            {
                return false;
            }

            DisposeWatchers();
            foreach (var entry in scanned)
            {
                if (!entry.Content.Available)
                {
                    Log.Change("screensaver.source." + entry.Source.Id,
                        $"Screensaver '{entry.Source.Name}' unavailable: {entry.Content.Detail}");
                    continue;
                }

                Watch(entry.Source);
            }

            lock (_state)
            {
                previous = _entries;
                _entries = scanned;
                _lastMissingScan = Stopwatch.GetTimestamp();
            }

            next = scanned;
            return true;
        }
        finally
        {
            _scan.Release();
        }
    }

    /// <summary>Registers a new image folder or scene pack.</summary>
    /// <param name="name">The name; unique, ignoring case.</param>
    /// <param name="folder">An absolute folder on a local or removable drive.</param>
    /// <param name="pack">Whether the folder is a scene pack rather than images.</param>
    internal void Add(string name, string folder, bool pack)
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

    /// <summary>Renames a source; its id, folder and options stay.</summary>
    /// <param name="id">The source.</param>
    /// <param name="name">The new name; unique, ignoring case.</param>
    /// <returns>The name as saved, trimmed.</returns>
    internal string Rename(string id, string name)
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
        return name;
    }

    /// <summary>Points a source at another folder; its other preferences stay.</summary>
    /// <param name="id">The source.</param>
    /// <param name="folder">An absolute folder on a local or removable drive.</param>
    internal void Relocate(string id, string folder)
    {
        var replacement = ValidateFolder(folder);
        _persist(config =>
        {
            var source = config.Sources.FirstOrDefault(source => source.Id == id)
                         ?? throw new InvalidOperationException("This source was removed.");
            source.Folder = replacement;
        });
    }

    /// <summary>Changes one playback option of a source.</summary>
    /// <param name="command">The command naming the source, the option and its value.</param>
    internal void Configure(SteamScreensaverContentCommand command)
    {
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
    }

    /// <summary>Forgets a source; its folder is left as it is.</summary>
    /// <param name="id">The source.</param>
    internal void Remove(string id)
    {
        _persist(config => config.Sources.RemoveAll(source => source.Id == id));
    }

    /// <summary>A registered source's cover, read on first request and cached until its content changes.</summary>
    /// <param name="id">The source.</param>
    /// <param name="cancellationToken">Cancels waiting and reading; the caller links the session's lifetime.</param>
    /// <returns>The JPEG bytes, or null for an unknown source, no preview or an unreadable file.</returns>
    internal async Task<byte[]?> ReadThumbnailAsync(string id, CancellationToken cancellationToken)
    {
        await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = Entries;
            var entry = entries.FirstOrDefault(item => item.Source.Id == id);
            var currentIds = entries.Select(item => item.Source.Id).ToHashSet(StringComparer.Ordinal);
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
            var bytes = await Task.Run(() => ScreensaverThumbnail.Read(path), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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

    private bool MissingSourceDue()
    {
        lock (_state)
        {
            var interval = _entries.Any(entry => !entry.Content.Available) ? 10 : 60;
            return _entries.Length > 0
                   && Stopwatch.GetElapsedTime(_lastMissingScan) >= TimeSpan.FromSeconds(interval);
        }
    }

    private bool ActiveFolderMissing(string? activeSourceId)
    {
        string? folder;
        lock (_state)
        {
            folder = activeSourceId is null
                ? null
                : _entries.FirstOrDefault(entry => entry.Source.Id == activeSourceId && entry.Content.Available)?.Source.Folder;
        }

        // Moving or unplugging the watched root need not produce an event inside that root.
        // Check the one active source on the existing host publication, without another poller.
        return folder is not null && !Directory.Exists(folder);
    }

    private void Watch(ScreensaverSourceConfig source)
    {
        try
        {
            var watcher = new FileSystemWatcher(source.Folder)
            {
                IncludeSubdirectories = source.Pack,
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
            Log.Change("screensaver.watch." + source.Id, "Screensaver folder watch unavailable: " + error.Message);
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

    private static ScreensaverSourceConfig Clone(ScreensaverSourceConfig source)
    {
        return new ScreensaverSourceConfig
        {
            Id = source.Id, Name = source.Name, Folder = source.Folder, Pack = source.Pack,
            Shuffle = source.Shuffle, IntervalSeconds = source.IntervalSeconds, Fill = source.Fill,
            FramesPerSecond = source.FramesPerSecond
        };
    }
}
