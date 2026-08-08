using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WSGM.Core;

/// <summary>Loads and atomically saves WSGM's shared per-user configuration file.</summary>
public static class ConfigStore
{
    /// <summary>Absolute path of the persisted configuration file.</summary>
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

    /// <summary>Loads the current configuration, returning safe defaults when the
    /// file is absent, malformed, or inaccessible.</summary>
    /// <returns>A normalized configuration that callers can use without null checks.</returns>
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
        config.AccentColor ??= "#FFFF9D3D";
        config.Splash ??= new SplashConfig();
        NormalizeSplash(config.Splash);
        return config;
    }

    // Bounds for every numeric splash field, mirrored 1:1 from the Appearance
    // editor's NumericUpDown Minimum/Maximum values (Settings\Pages\AppearancePage.axaml).
    // The editor can never produce anything outside them — but a shared .wsgmsplash
    // theme and a hand-edited config.json can, and the splash renderer only
    // lower-bounds its inputs, so "SpinnerSize": 2147483647 would explode layout
    // before the boot cover is usable. Clamping here covers BOTH untrusted paths:
    // config load (via Normalize) and theme import (SplashTheme.Import).
    private const int MinFontSize = 1;
    private const int MaxTitleFontSize = 400;
    private const int MaxCaptionFontSize = 200;
    private const int MinSpinnerSize = 1;
    private const int MaxSpinnerSize = 1024;
    private const int MinLogoMaxSize = 1;
    private const int MaxLogoMaxSize = 4096;
    private const int MinPadding = 0;
    private const int MaxPadding = 4096;
    private const int MinAbsoluteCoordinate = 0;
    private const int MaxAbsoluteCoordinate = 16384;

    /// <summary>Repairs explicit JSON nulls inside a splash section (see
    /// <see cref="Normalize"/>) and clamps every numeric field into the range the
    /// Appearance editor enforces. Shared with splash-theme import, which
    /// deserializes the same contract from untrusted archives.</summary>
    internal static SplashConfig NormalizeSplash(SplashConfig splash)
    {
        splash.Text ??= "Please wait";
        splash.TextColor ??= "#FFFFFF";
        splash.Caption ??= "";
        splash.CaptionColor ??= "#666666";
        splash.SpinnerColor ??= "#FFFFFF";
        splash.BackgroundColor ??= "#000000";
        // "No image" has exactly one representation, "": every consumer tests these
        // with IsNullOrWhiteSpace, so a hand-edited config or an imported theme
        // carrying "   " means no image — and must not be persisted as whitespace by
        // the next save either (SplashAssets.PrepareSlot normalizes the same way).
        splash.BackgroundImagePath = Blank(splash.BackgroundImagePath);
        splash.LogoImagePath = Blank(splash.LogoImagePath);
        splash.TextPlacement ??= new SplashElementPlacement();
        splash.SpinnerPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };
        splash.LogoPlacement ??= new SplashElementPlacement { Mode = SplashPlacementMode.WithText };

        splash.TitleFontSize = Math.Clamp(splash.TitleFontSize, MinFontSize, MaxTitleFontSize);
        splash.CaptionFontSize = Math.Clamp(splash.CaptionFontSize, MinFontSize, MaxCaptionFontSize);
        splash.SpinnerSize = Math.Clamp(splash.SpinnerSize, MinSpinnerSize, MaxSpinnerSize);
        splash.LogoMaxSize = Math.Clamp(splash.LogoMaxSize, MinLogoMaxSize, MaxLogoMaxSize);
        // A JSON number ("SpinnerStyle": 999) deserializes into the enum unchecked;
        // an unknown member falls back to the field's default rather than to whatever
        // neighbouring style a clamp would land on.
        if ((int)splash.SpinnerStyle is < 0 or > (int)SplashSpinnerStyle.Off)
        {
            splash.SpinnerStyle = SplashSpinnerStyle.Ring;
        }
        if ((int)splash.SweepEdge is < 0 or > (int)SweepEdge.Top)
        {
            splash.SweepEdge = SweepEdge.Bottom;
        }
        NormalizePlacement(splash.TextPlacement);
        NormalizePlacement(splash.SpinnerPlacement);
        NormalizePlacement(splash.LogoPlacement);
        return splash;
    }

    /// <summary>Maps a null or whitespace-only image path to the single "no image"
    /// value, leaving every real path untouched (leading/trailing spaces are legal
    /// in Windows path components, so nothing else is trimmed).</summary>
    private static string Blank(string? path) => string.IsNullOrWhiteSpace(path) ? "" : path;

    /// <summary>Clamps one element placement into the editor's ranges and drops
    /// unknown enum members back to their defaults.</summary>
    private static void NormalizePlacement(SplashElementPlacement placement)
    {
        if ((int)placement.Mode is < 0 or > (int)SplashPlacementMode.WithText)
        {
            placement.Mode = SplashPlacementMode.Anchor;
        }
        if ((int)placement.Anchor is < 0 or > (int)SplashPlacementAnchor.BottomRight)
        {
            placement.Anchor = SplashPlacementAnchor.Center;
        }
        placement.PaddingX = Math.Clamp(placement.PaddingX, MinPadding, MaxPadding);
        placement.PaddingY = Math.Clamp(placement.PaddingY, MinPadding, MaxPadding);
        // The editor's absolute X/Y spinners start at 0 (an element placed off the
        // top-left is unreachable, not a feature), so negatives clamp up to 0 here.
        placement.X = Math.Clamp(placement.X, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
        placement.Y = Math.Clamp(placement.Y, MinAbsoluteCoordinate, MaxAbsoluteCoordinate);
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

    /// <summary>Atomically persists a complete configuration snapshot.</summary>
    /// <param name="config">The configuration state to serialize.</param>
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

    /// <summary>Takes the cross-process config lock for a caller that must keep a
    /// whole read-modify-write sequence — plus the file work between its steps —
    /// atomic against other WSGM processes. SettingsViewModel.SaveMerged holds it
    /// across Load → Save → the splash-asset Commit → the boot-manifest write, so
    /// config.json and the live splash images can never be left describing different
    /// states. Only FAST operations belong in such a scope: the timeout below is
    /// sized for a small JSON write, so anything slow (the splash-asset staging
    /// copies, which can be tens of megabytes) must be done before the lock is taken.
    /// <para>The <see cref="Load"/> and <see cref="Save"/> calls made inside such a
    /// scope acquire the SAME lock again. Those nested acquisitions are FREE: a
    /// thread-local depth counter short-circuits them, so they neither touch the
    /// kernel object nor — and this is the point — pay the
    /// <see cref="MutexTimeoutMs"/> timeout a second, third and fourth time when
    /// another process holds the lock. Relying on the Win32 mutex's own per-thread
    /// recursion count instead made a contended save cost one timeout per nested call
    /// (Load + Save + repair Save + the outer scope ≈ 6-8 s of frozen UI and four
    /// "Config mutex timed out" lines). Only the OUTERMOST scope releases, so the hold
    /// survives until this scope is disposed, and the degraded no-lock path is
    /// inherited by the nested calls — acquiring the lock for one step of a sequence
    /// whose outer scope already gave up would not restore any guarantee.</para></summary>
    /// <returns>A scope that releases the lock when disposed.</returns>
    internal static IDisposable AcquireLock() => ConfigMutex.Acquire();

    /// <summary>Test seam: how deeply the CALLING thread currently holds the config
    /// lock (0 = not held). Exists so the acquire/release balance of the nested scopes
    /// can be asserted without going near the per-user config file.</summary>
    internal static int LockDepth => ConfigMutex.CurrentDepth;

    /// <summary>Cross-process guard around Load/Save. Failure to create or acquire
    /// the mutex degrades to lock-less operation with a warning — never a deadlock.
    /// Re-entrant per thread through a depth counter: only the outermost scope talks
    /// to the kernel object, so a nested acquisition costs nothing even while another
    /// process holds the lock.
    /// <para>Scopes are meant to be disposed in reverse acquisition order (they are
    /// all <c>using</c> blocks today). Out-of-order disposal is a caller error, and
    /// what is guaranteed for it is only that the state stays sound: the depth never
    /// goes negative, a late nested Dispose cannot pop a level it does not own, and
    /// the mutex is released exactly once — by the scope that acquired it, at the
    /// moment that scope is disposed. Cross-process exclusion consequently ENDS
    /// there: a nested scope that outlives its owner holds nothing, and the counter
    /// stops pretending otherwise rather than blocking a later real acquisition.</para></summary>
    private sealed class ConfigMutex : IDisposable
    {
        // Per-thread lock state. The mutex itself is thread-owned in Win32, so the
        // depth can only ever describe the thread that took it; a nested acquisition
        // from ANOTHER thread is a real, competing acquisition and is treated as one.
        [ThreadStatic]
        private static int _depth;

        private readonly Mutex? _mutex;
        private readonly bool _owned;
        private readonly bool _nested;

        // The depth this scope established (1 for the outermost). Dispose pops back
        // to _level - 1 instead of blindly decrementing, which is what keeps the
        // counter sane when scopes are disposed OUT OF ORDER (see Dispose).
        private readonly int _level;
        private bool _disposed;

        private ConfigMutex(Mutex? mutex, bool owned, bool nested, int level)
        {
            _mutex = mutex;
            _owned = owned;
            _nested = nested;
            _level = level;
        }

        /// <summary>How deeply the calling thread holds the lock (0 = not at all).</summary>
        internal static int CurrentDepth => _depth;

        public static ConfigMutex Acquire()
        {
            if (_depth > 0)
            {
                // Already held by this thread (SaveMerged's scope around Load/Save):
                // no kernel call, and above all no second MutexTimeoutMs wait.
                _depth++;
                return new ConfigMutex(null, owned: false, nested: true, level: _depth);
            }

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
                    // The wait DID succeed, so this scope owns the mutex and must release it.
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
            // Counted even when the acquisition degraded, so the nested steps of one
            // sequence inherit that decision instead of each paying the timeout again.
            _depth = 1;
            return new ConfigMutex(mutex, owned, nested: false, level: 1);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                // Balance is per scope: a double Dispose (an explicit one plus the
                // `using`) must not pop a depth level its scope never pushed.
                return;
            }
            _disposed = true;

            // Pop to this scope's own level rather than decrementing blindly. Scopes
            // are meant to nest strictly (`using` blocks), but disposing them OUT OF
            // ORDER used to drive the counter negative — the outermost scope assigned
            // 0 while a nested one was still live, and that nested Dispose then made
            // it -1, after which the next Acquire on this thread took the slow kernel
            // path even though the lock was free, and a later nested scope could pop
            // an unrelated real acquisition. The guard covers both directions:
            //   • depth still at or above this level → this scope is the deepest one
            //     that is still counted, so its level - 1 is the correct new depth;
            //   • depth already BELOW it → an outer scope was disposed first and has
            //     reset the counter (possibly for a fresh acquisition since), so this
            //     late Dispose must not touch it at all.
            if (_depth >= _level)
            {
                _depth = _level - 1;
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
                // Never let lock cleanup break a save/load path — but never let it skip
                // the handle either: a swallowed ReleaseMutex failure used to leave the
                // Mutex undisposed AND the named object owned, after which every other
                // WSGM process ran the degraded lock-less path for the rest of the
                // session. Closing the handle abandons the mutex instead, which the
                // next waiter gets (as AbandonedMutexException) immediately.
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
    }
}
