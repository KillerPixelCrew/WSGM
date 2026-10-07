using System;
using System.IO;
using System.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Refreshes library badges when Steam changes its library registrations or names.</summary>
internal sealed class LibraryBadgeWatcher : IDisposable
{
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private readonly ConfigStore _store;
    private readonly FileSystemWatcher _watcher;
    private bool _disposed;

    private LibraryBadgeWatcher(ConfigStore store, string directory, string fileName)
    {
        _store = store;
        _debounce = new Timer(Refresh, null, Timeout.Infinite, Timeout.Infinite);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Changed += (_, _) => Debounce();
            watcher.Created += (_, _) => Debounce();
            watcher.Deleted += (_, _) => Debounce();
            watcher.Renamed += (_, _) => Debounce();
            watcher.Error += (_, _) => Debounce();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            lock (_gate)
            {
                _disposed = true;
                watcher?.Dispose();
                _debounce.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounce.Dispose();
            _watcher.Dispose();
        }
    }

    internal static LibraryBadgeWatcher? StartNew(ConfigStore store)
    {
        try
        {
            Steam.TryReadLibraryFolders(out var path, out _);
            var directory = path is null ? null : Path.GetDirectoryName(path);
            if (path is null || directory is null || !Directory.Exists(directory))
            {
                return null;
            }

            return new LibraryBadgeWatcher(store, directory, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"Library badge watcher: could not start: {ex.Message}");
            return null;
        }
    }

    private void Debounce()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _debounce.Change(500, Timeout.Infinite);
            }
        }
    }

    private void Refresh(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                LibraryBadges.Update(_store.Read().RequireConfig(), LibraryTabManager.PresentCardContentIds());
            }
            catch (Exception ex)
            {
                Log.Warn($"Library badge refresh failed: {ex.Message}");
            }
        }
    }
}
