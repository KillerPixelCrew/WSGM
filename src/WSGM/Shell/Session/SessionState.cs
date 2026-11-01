using System;
using System.Text.Json;
using System.Threading;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>
///     The live state every part of the resident session reads: the configuration in force, the
///     shell mode, and whether shutdown has begun, with the session's one cancellation.
/// </summary>
/// <remarks>
///     One instance per <see cref="ShellSession" />, handed to each part it composes. It holds state,
///     not services: the parts own their services and read these flags on every look, so a reload or a
///     mode change reaches them without being rebuilt.
/// </remarks>
internal sealed class SessionState
{
    private readonly CancellationTokenSource _shutdownCancellation = new();

    // True for the direct game-mode boot; the desktop-resume paths clear it, and
    // DesktopModeStarting/GameModeEntered keep it current afterwards.
    private volatile bool _inGameMode = true;

    private volatile bool _shutdownRequested;

    /// <summary>Creates the state of a session that has not started yet.</summary>
    /// <param name="config">The configuration to apply when the session starts.</param>
    /// <param name="store">The process-owned persistence and data roots.</param>
    /// <param name="overlayTestOnly">Whether this is the manual overlay test, which changes nothing outside WSGM.</param>
    internal SessionState(AppConfig config, ConfigStore store, bool overlayTestOnly)
    {
        Config = config;
        Store = store ?? throw new ArgumentNullException(nameof(store));
        OverlayTestOnly = overlayTestOnly;
    }

    /// <summary>The process-owned persistence and data roots.</summary>
    internal ConfigStore Store { get; }

    /// <summary>Whether this is the manual overlay test, which starts no apps and never talks to Steam.</summary>
    internal bool OverlayTestOnly { get; }

    /// <summary>
    ///     The configuration in force. Replaced wholesale on every reload, on the UI thread, so it stays
    ///     the same instance the overlay, SessionModes and DisplayScale's saved-scale snapshot live on.
    /// </summary>
    internal AppConfig Config { get; set; }

    /// <summary>Whether the session is in Game Mode.</summary>
    internal bool InGameMode
    {
        get => _inGameMode;
        set => _inGameMode = value;
    }

    /// <summary>
    ///     Whether shutdown has begun. Set on the UI thread, where startup builds the owners, so a
    ///     startup that has not reached the UI thread yet sees it and builds nothing.
    /// </summary>
    internal bool ShutdownRequested => _shutdownRequested;

    /// <summary>The session's one cancellation, cancelled during shutdown and never disposed.</summary>
    internal CancellationToken ShutdownToken => _shutdownCancellation.Token;

    /// <summary>Closes admission for new work; the first step of shutdown.</summary>
    internal void RequestShutdown()
    {
        _shutdownRequested = true;
    }

    /// <summary>Cancels the session's work. The source stays undisposed: work left behind may still read it.</summary>
    internal void CancelWork()
    {
        // ReSharper disable once MethodHasAsyncOverload
        _shutdownCancellation.Cancel();
    }

    /// <summary>Saves one change made from a WSGM surface outside Settings.</summary>
    /// <param name="change">The field to write, applied to a fresh strict load.</param>
    /// <param name="boot">Whether boot.json follows the change, as it does for the start settings.</param>
    /// <returns>What was persisted.</returns>
    /// <remarks>
    ///     One transaction, as WSGM Settings saves: the store's lock is held across the write and the
    ///     boot manifest, so the service never starts WSGM from a manifest older than the config. The
    ///     writer supplies its read result to boot projection without nesting store operations.
    /// </remarks>
    internal AppConfig CommitSetting(Action<AppConfig> change, bool boot)
    {
        using (var transaction = Store.Transaction())
        {
            var persisted = transaction.Config;
            var before = JsonSerializer.Serialize(persisted, ConfigJsonContext.Tolerant.AppConfig);
            change(persisted);
            var after = JsonSerializer.Serialize(persisted, ConfigJsonContext.Tolerant.AppConfig);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                transaction.Save();
            }

            if (boot)
            {
                BootManifestWriter.WriteCurrent(transaction.Read, Store.Context);
            }

            return persisted;
        }
    }
}
