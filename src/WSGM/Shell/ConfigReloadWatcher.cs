using System;
using System.IO;
using System.Threading;
using Avalonia.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     Reloads config.json after an external write and hands each good read to the UI thread.
///     A failed read keeps the running state.
/// </summary>
internal sealed class ConfigReloadWatcher : IDisposable
{
    private readonly Action<AppConfig> _apply;
    private readonly Timer _debounce;
    private readonly Lock _gate = new();

    // Reads run one at a time, so a later read never posts before an earlier one.
    private readonly Lock _readGate = new();
    private readonly ConfigStore _store;

    // Field-rooted deliberately: an unreferenced enabled FileSystemWatcher is
    // GC-collectible (it holds only a WeakReference to itself in its pending
    // ReadDirectoryChangesW state) and silently stops raising events.
    private readonly FileSystemWatcher _watcher;
    private volatile bool _disposed;

    /// <summary>Starts watching the store's config.json.</summary>
    /// <param name="store">The store whose file is watched and read.</param>
    /// <param name="apply">Applies a successfully read configuration on the UI thread.</param>
    internal ConfigReloadWatcher(ConfigStore store, Action<AppConfig> apply)
    {
        _store = store;
        _apply = apply;
        _debounce = new Timer(Reload, null, Timeout.Infinite, Timeout.Infinite);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(store.Context.Root, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            watcher.Changed += (_, _) => Debounce();
            watcher.Renamed += (_, _) => Debounce();
            watcher.Error += OnError;
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

    /// <summary>Stops watching; a reload already read is no longer applied.</summary>
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
            try
            {
                _watcher.EnableRaisingEvents = false;
            }
            finally
            {
                _watcher.Dispose();
            }
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

    // Internal-buffer overflow or a directory-level error kills the change
    // events silently, and settings would stop applying for the rest of the
    // session with nothing in the log to diagnose it from. Log, reload once
    // (the missed write is already on disk), and re-arm by restarting the
    // watch. Deliberately NOT a recreate: this handler would resubscribe
    // itself and a persistently failing directory would spin.
    private void OnError(object sender, ErrorEventArgs e)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                Log.Warn($"Config watcher error: {e.GetException().Message} — re-arming.");
                _debounce.Change(500, Timeout.Infinite);
                _watcher.EnableRaisingEvents = false;
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Config watcher could not be re-armed: {ex.Message}");
        }
    }

    // The LOAD stays off the UI thread: it takes the cross-process config
    // mutex (2 s timeout) that a settings save holds across the write, the
    // splash-asset promotion and the boot manifest, and 500 ms of debounce does
    // not reliably outlast that. Only the cheap, UI-affine apply is posted.
    private void Reload(object? state)
    {
        lock (_readGate)
        {
            if (_disposed)
            {
                return;
            }

            ConfigReadResult read;
            try
            {
                read = _store.Read();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Config reload skipped: {ex.Message}; the running state is retained.");
                return;
            }

            if (read.Config is not { } config)
            {
                Log.Warn($"Config reload skipped: {read.Outcome}; the running state is retained.");
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    _apply(config);
                }
            });
        }
    }
}
