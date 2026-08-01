using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

public static class ConfigStore
{
    public static string ConfigPath => Path.Combine(Log.Directory, "config.json");

    // Shell, settings window, and elevated one-shots all load-modify-save the same
    // file; the named mutex serializes the individual Load/Save calls so they never
    // interleave. It CANNOT merge: saving an AppConfig loaded long ago overwrites
    // every field another process persisted in between, so long-lived holders must
    // re-load and re-apply only their own fields before saving (see
    // SettingsViewModel.SaveMerged). The timeout is short and a miss only logs —
    // recovery paths must never block here.
    private const string MutexName = @"Local\WSGM.Config";
    private const int MutexTimeoutMs = 2000;

    public static AppConfig Load()
    {
        using var guard = ConfigMutex.Acquire();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (config is not null)
                {
                    return Normalize(config);
                }
            }
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

    /// <summary>An explicit JSON null ("StartupApps": null) deserializes over the
    /// property initializer; replace nulls with fresh defaults so a hand-edited
    /// config can never NRE the shell later (which would kill it before the panic
    /// handler runs). New nested object/list members belong in this list too.</summary>
    internal static AppConfig Normalize(AppConfig config)
    {
        config.StartupApps ??= [];
        config.Hotkey ??= new HotkeyConfig();
        config.GamepadChord ??= new GamepadChordConfig();
        config.Gestures ??= new GestureConfig();
        config.SavedDisplayScales ??= [];
        config.SavedDisplayScaleEntries ??= [];
        config.PreviousConsoleLockSchemeValues ??= [];
        return config;
    }

    private static void PreserveCorruptFile()
    {
        try
        {
            var bad = Path.Combine(Log.Directory, "config.bad.json");
            File.Copy(ConfigPath, bad, overwrite: true);
            Log.Error($"Corrupt config preserved at {bad} — registry snapshots may be recoverable from it.");
        }
        catch
        {
            // Best effort — an unreadable file cannot be preserved either.
        }
    }

    public static void Save(AppConfig config)
    {
        using var guard = ConfigMutex.Acquire();
        Directory.CreateDirectory(Log.Directory);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        // Per-process temp name so concurrent savers never share it; a leftover
        // .tmp from a failed rename is harmlessly overwritten by that process later.
        var temp = $"{ConfigPath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, json);
        // Atomic replace (MoveFileEx REPLACE_EXISTING) — covers both the exists and
        // not-yet-exists cases without a TOCTOU window.
        File.Move(temp, ConfigPath, overwrite: true);
    }

    /// <summary>Cross-process guard around Load/Save. Failure to create or acquire
    /// the mutex degrades to lock-less operation with a warning — never a deadlock.</summary>
    private sealed class ConfigMutex : IDisposable
    {
        private readonly Mutex? _mutex;
        private readonly bool _owned;

        private ConfigMutex(Mutex? mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        public static ConfigMutex Acquire()
        {
            Mutex? mutex = null;
            var owned = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, MutexName);
                try
                {
                    owned = mutex.WaitOne(MutexTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    // Previous holder died mid-section; Save is atomic, the file is intact.
                    owned = true;
                }
                if (!owned)
                {
                    Log.Warn("Config mutex timed out — proceeding without cross-process lock.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Config mutex unavailable, proceeding without lock: {ex.Message}");
            }
            return new ConfigMutex(mutex, owned);
        }

        public void Dispose()
        {
            try
            {
                if (_owned)
                {
                    _mutex?.ReleaseMutex();
                }
                _mutex?.Dispose();
            }
            catch
            {
                // Never let lock cleanup break a save/load path.
            }
        }
    }
}
