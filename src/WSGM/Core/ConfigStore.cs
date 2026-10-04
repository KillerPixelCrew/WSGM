using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using WindowsDeviceControl;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;

using static WSGM.Core.AppConfigDefaults;

namespace WSGM.Core;

/// <summary>Loads and atomically saves WSGM's shared per-user configuration file.</summary>
public sealed class ConfigStore
{
    /// <summary>Creates configuration persistence over explicit user paths and lock identity.</summary>
    /// <param name="context">The owner's filesystem and cross-process lock context.</param>
    public ConfigStore(UserDataContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>The filesystem and lock identity used by this store.</summary>
    public UserDataContext Context { get; }

    // Shell, settings window, and elevated one-shots all load-modify-save the same
    // file; the named mutex serializes the individual Load/Save calls so they never
    // interleave. It CANNOT merge: saving an AppConfig loaded long ago overwrites
    // every field another process persisted in between, so long-lived holders must
    // re-load and re-apply only their own fields before saving (see
    // SettingsViewModel.Save). Read-only startup may degrade after the short timeout; every
    // write and read-modify-write transaction fails closed instead of risking a lost update.
    private const int MutexTimeoutMs = 2000;

    /// <summary>Absolute path of the persisted configuration file.</summary>
    public string ConfigPath => Path.Combine(Context.Root, "config.json");

    /// <summary>
    ///     Test seam: how deeply the CALLING thread currently holds the config
    ///     lock (0 = not held). Exists so the acquire/release balance of the nested scopes
    ///     can be asserted without going near the per-user config file.
    /// </summary>
    internal int LockDepth => ConfigMutex.DepthFor(Context.ConfigMutexName);

    /// <summary>Whether the calling thread owns the named mutex.</summary>
    internal bool HasExclusiveLock => ConfigMutex.HasOwnership(Context.ConfigMutexName);

    /// <summary>
    ///     Loads the current configuration, returning safe defaults when the
    ///     file is absent, malformed, or inaccessible.
    /// </summary>
    /// <returns>A normalized configuration that callers can use without null checks.</returns>
    public AppConfig Load()
    {
        using var guard = ConfigMutex.Acquire(Context.ConfigMutexName, false);
        try
        {
            return LoadCurrentDocument();
        }
        catch (Exception ex)
        {
            // The file holds the previous-shell/UAC/lock-screen registry snapshots;
            // set the corrupt file aside so they stay manually recoverable instead
            // of being clobbered when the next Save writes blank defaults.
            Log.Error("Failed to load config, using defaults", ex);
            PreserveCorruptFile();
        }

        return new AppConfig();
    }

    /// <summary>
    ///     Loads configuration for a read-modify-write transaction. Unlike
    ///     <see cref="Load" />, an existing unreadable file is never converted to defaults:
    ///     the exception aborts the mutation so registry recovery snapshots cannot be erased.
    /// </summary>
    /// <returns>The normalized configuration, or defaults only when no file exists.</returns>
    internal AppConfig LoadForMutation()
    {
        using var guard = ConfigMutex.Acquire(Context.ConfigMutexName, true);
        return LoadCurrentDocument();
    }

    private AppConfig LoadCurrentDocument()
    {
        if (!File.Exists(ConfigPath))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(ConfigPath);
        var config = ConfigRepair.Deserialize(json);
        var normalized = AppConfigRules.Normalize(config);
        foreach (var diagnostic in normalized.Diagnostics)
        {
            Log.Warn(diagnostic);
        }

        return normalized.Value;
    }

    private void PreserveCorruptFile()
    {
        try
        {
            // This runs in the elevated one-shots too (UacSettings/LockScreenSettings
            // call Load), and %LOCALAPPDATA%\WSGM is writable by the unelevated user:
            // a pre-planted reparse point at a PREDICTABLE destination would redirect
            // an overwriting elevated copy (CopyFileEx follows destination links). An
            // unpredictable name cannot be pre-planted, and CreateNew refuses to write
            // through anything that already occupies it — no overwrite, no follow.
            var bad = Path.Combine(Context.Root, $"config.bad.{Guid.NewGuid():N}.json");
            using (var source = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var dest = new FileStream(bad, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(dest);
            }

            Log.Error($"Corrupt config preserved at {bad} — registry snapshots may be recoverable from it.");
            PruneCorruptFiles();
        }
        catch
        {
            // Best effort — an unreadable file cannot be preserved either.
        }
    }

    /// <summary>
    ///     Keeps only the newest few preserved copies. Every Load of a broken
    ///     config writes another uniquely named one — several per boot across the shell,
    ///     Settings and the elevated one-shots — and nothing else ever reclaims them.
    ///     Deleting by enumerated exact name keeps the unpredictable-name property that
    ///     makes the write itself reparse-point safe.
    /// </summary>
    private void PruneCorruptFiles()
    {
        const int keep = 5;
        try
        {
            var stale = new DirectoryInfo(Context.Root)
                .GetFiles("config.bad.*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);
            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not prune {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not prune preserved configs: {ex.Message}");
        }
    }

    /// <summary>Atomically persists a complete configuration snapshot.</summary>
    /// <param name="config">The configuration state to serialize.</param>
    public void Save(AppConfig config)
    {
        using var guard = ConfigMutex.Acquire(Context.ConfigMutexName, true);
        Directory.CreateDirectory(Context.Root);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Tolerant.AppConfig);
        // A unique orphan is harmless and can be diagnosed from this bounded warning.
        AtomicFile.WriteText(ConfigPath, json, false, static (temp, ex) =>
            Log.Warn($"Config temp cleanup failed for '{Path.GetFileName(temp)}': {ex.Message}"));
    }

    /// <summary>
    ///     The only supported read-modify-write path for config.json: takes the
    ///     cross-process lock, loads through <see cref="LoadForMutation" />, applies
    ///     <paramref name="mutate" />, and saves — all inside one scope, so no other WSGM
    ///     process can persist between the read and the write and have its fields dropped
    ///     by it. Callers must apply ONLY their own fields: everything else in the loaded
    ///     instance is written straight back.
    ///     <para>
    ///         The strict load is the point. <see cref="Load" /> answers an unreadable
    ///         file with defaults, which is right for a reader but catastrophic here — saving
    ///         those defaults erases the previous-shell/UAC/lock-screen registry snapshots
    ///         uninstall restores from. An unreadable existing file therefore throws out of
    ///         this method and ABORTS the mutation; <see cref="Load" /> stays available for
    ///         read-only callers.
    ///     </para>
    ///     <para>
    ///         A caller that needs more work under the same lock (see
    ///         SettingsViewModel's save transaction, which also promotes splash assets and writes the
    ///         boot manifest) wraps this in its own <see cref="AcquireLock" /> scope — the
    ///         nested acquisition is free.
    ///     </para>
    /// </summary>
    /// <param name="mutate">Applies the caller's fields to the freshly loaded configuration.</param>
    /// <returns>The configuration instance that was persisted.</returns>
    /// <exception cref="InvalidDataException">The existing file could not be parsed.</exception>
    internal AppConfig Mutate(Action<AppConfig> mutate)
    {
        using var guard = ConfigMutex.Acquire(Context.ConfigMutexName, true);
        var config = LoadForMutation();
        mutate(config);
        Save(config);
        return config;
    }

    /// <summary>
    ///     Takes the cross-process config lock for a caller that must keep a
    ///     whole read-modify-write sequence — plus the file work between its steps —
    ///     atomic against other WSGM processes. SettingsViewModel.Save holds it
    ///     across Load → Save → the splash-asset Commit → the boot-manifest write, so
    ///     config.json and the live splash images can never be left describing different
    ///     states. Only FAST operations belong in such a scope: the timeout below is
    ///     sized for a small JSON write, so anything slow (the splash-asset staging
    ///     copies, which can be tens of megabytes) must be done before the lock is taken.
    ///     <para>
    ///         The <see cref="Load" /> and <see cref="Save" /> calls made inside such a
    ///         scope acquire the SAME lock again. Those nested acquisitions are FREE: a
    ///         thread-local depth counter short-circuits them, so they neither touch the
    ///         kernel object nor — and this is the point — pay the
    ///         <see cref="MutexTimeoutMs" /> timeout a second, third and fourth time when
    ///         another process holds the lock. Relying on the Win32 mutex's own per-thread
    ///         recursion count instead made a contended save cost one timeout per nested call
    ///         (Load + Save + repair Save + the outer scope ≈ 6-8 s of frozen UI and four
    ///         "Config mutex timed out" lines). Only the OUTERMOST scope releases, so the hold
    ///         survives until this scope is disposed. Write transactions never enter a
    ///         degraded scope: timeout or mutex failure aborts them.
    ///     </para>
    /// </summary>
    /// <returns>A scope that releases the lock when disposed.</returns>
    internal IDisposable AcquireLock()
    {
        return ConfigMutex.Acquire(Context.ConfigMutexName, true);
    }

    /// <summary>
    ///     Cross-process guard around Load/Save. Read-only loads may degrade
    ///     with a warning; writes fail closed when the mutex cannot be acquired.
    ///     Re-entrant per thread through a depth counter: only the outermost scope talks
    ///     to the kernel object, so a nested acquisition costs nothing even while another
    ///     process holds the lock.
    ///     <para>
    ///         Scopes are meant to be disposed in reverse acquisition order (they are
    ///         all <c>using</c> blocks today). Out-of-order disposal is a caller error, and
    ///         what is guaranteed for it is only that the state stays sound: the depth never
    ///         goes negative, a late nested Dispose cannot pop a level it does not own, and
    ///         the mutex is released exactly once — by the scope that acquired it, at the
    ///         moment that scope is disposed. Cross-process exclusion consequently ENDS
    ///         there: a nested scope that outlives its owner holds nothing, and the counter
    ///         stops pretending otherwise rather than blocking a later real acquisition.
    ///     </para>
    /// </summary>
    private sealed class ConfigMutex : IDisposable
    {
        // The depth this scope established (1 for the outermost). Dispose pops back
        // to _level - 1 instead of blindly decrementing, which is what keeps the
        // counter sane when scopes are disposed OUT OF ORDER (see Dispose).
        private readonly int _level;
        // Per-thread lock state. The mutex itself is thread-owned in Win32, so the
        // depth can only ever describe the thread that took it; a nested acquisition
        // from ANOTHER thread is a real, competing acquisition and is treated as one.

        private readonly Mutex? _mutex;
        private readonly bool _nested;
        private readonly bool _owned;
        private readonly string _name;
        private readonly ThreadState _state;
        private readonly int _threadId;
        private bool _disposed;

        private ConfigMutex(string name, ThreadState state, Mutex? mutex, bool owned, bool nested, int level)
        {
            _name = name;
            _state = state;
            _threadId = Environment.CurrentManagedThreadId;
            _mutex = mutex;
            _owned = owned;
            _nested = nested;
            _level = level;
        }

        /// <summary>How deeply the calling thread holds the lock (0 = not at all).</summary>
        [ThreadStatic] private static Dictionary<string, ThreadState>? _states;

        private int CurrentDepth
        {
            get => _state.Depth;
            set => _state.Depth = value;
        }

        private bool HasExclusiveOwnership
        {
            get => _state.Exclusive;
            set => _state.Exclusive = value;
        }

        internal static int DepthFor(string name)
        {
            return _states is not null && _states.TryGetValue(name, out var state) ? state.Depth : 0;
        }

        internal static bool HasOwnership(string name)
        {
            return _states is not null && _states.TryGetValue(name, out var state) && state.Exclusive;
        }

        private sealed class ThreadState
        {
            internal int Depth;
            internal bool Exclusive;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                // Balance is per scope: a double Dispose (an explicit one plus the
                // `using`) must not pop a depth level its scope never pushed.
                return;
            }

            if (Environment.CurrentManagedThreadId != _threadId)
            {
                throw new InvalidOperationException("The config mutex scope must retire on its acquiring thread.");
            }

            _disposed = true;

            // Each scope owns one recorded depth. A late out-of-order Dispose must not pop a newer
            // acquisition, so it changes depth only while its own level is still counted.
            if (CurrentDepth >= _level)
            {
                CurrentDepth = _level - 1;
                if (CurrentDepth == 0)
                {
                    HasExclusiveOwnership = false;
                    if (_states is not null && _states.TryGetValue(_name, out var current)
                        && ReferenceEquals(current, _state))
                    {
                        _states.Remove(_name);
                    }
                }
            }

            if (_nested)
            {
                // Nested scopes never touch the kernel object; only the scope that
                // acquired the mutex releases it, exactly once.
                return;
            }

            try
            {
                if (_owned)
                {
                    _mutex?.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                // Cleanup failure does not replace the save/load outcome. The handle is still
                // disposed below so the next waiter observes abandonment instead of a stuck owner.
                Log.Warn($"Config mutex release failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    _mutex?.Dispose();
                }
                catch
                {
                    // Closing a handle: nothing left to fall back to.
                }
            }
        }

        public static ConfigMutex Acquire(string name, bool requireExclusive)
        {
            _states ??= new Dictionary<string, ThreadState>(StringComparer.Ordinal);
            if (!_states.TryGetValue(name, out var state))
            {
                state = new ThreadState();
                _states.Add(name, state);
            }

            if (state.Depth > 0)
            {
                if (requireExclusive && !state.Exclusive)
                {
                    throw new InvalidOperationException(
                        "An exclusive config operation cannot be nested inside a degraded read.");
                }

                // Already held by this thread (Settings Save's scope around Load/Save):
                // no kernel call, and above all no second MutexTimeoutMs wait.
                state.Depth++;
                return new ConfigMutex(name, state, null, false, true, state.Depth);
            }

            Mutex? mutex = null;
            var owned = false;
            try
            {
                mutex = new Mutex(false, name);
                try
                {
                    owned = mutex.WaitOne(MutexTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    // Previous holder died mid-section; Save is atomic, the file is intact.
                    // The wait DID succeed, so this scope owns the mutex and must release it.
                    owned = true;
                }

                if (!owned)
                {
                    if (requireExclusive)
                    {
                        throw new TimeoutException(
                            "The shared WSGM configuration is busy; the save was not performed.");
                    }

                    Log.Warn("Config mutex timed out — continuing with a read-only snapshot.");
                }
            }
            catch (Exception ex)
            {
                if (requireExclusive)
                {
                    mutex?.Dispose();
                    _states.Remove(name);
                    throw;
                }

                Log.Warn($"Config mutex unavailable for read-only load: {ex.Message}");
            }

            // Counted even when the acquisition degraded, so the nested steps of one
            // sequence inherit that decision instead of each paying the timeout again.
            state.Depth = 1;
            state.Exclusive = owned;
            return new ConfigMutex(name, state, mutex, owned, false, 1);
        }
    }
}
