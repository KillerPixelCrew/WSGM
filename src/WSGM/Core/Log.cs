using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Severity of one log line, and the order the verbosity threshold compares against.</summary>
public enum LogLevel
{
    /// <summary>Detail worth keeping only while investigating. Suppressed unless verbose.</summary>
    Debug,

    /// <summary>A state transition or lifecycle event worth having in every log.</summary>
    Info,

    /// <summary>Something degraded, was refused, or fell back: behaviour actually changed.</summary>
    Warn,

    /// <summary>A failure the code could not handle.</summary>
    Error,
}

/// <summary>How much detail the log records, as a user-facing choice.</summary>
/// <remarks>
/// Two states rather than a full level picker: the only useful question is whether debug detail is
/// wanted. Hiding warnings or failures is never a reasonable choice, because this log is the whole
/// of remote diagnosis.
/// </remarks>
public enum LogVerbosity
{
    /// <summary>Transitions, warnings and failures. The default.</summary>
    Normal,

    /// <summary>Adds the debug level, for reproducing a specific problem.</summary>
    Verbose,
}

/// <summary>Tiny synchronized file logger. No toasts/taskbar exist in shell mode,
/// so the log file is the primary diagnostic surface.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static string _name = "wsgm";

    // Below this level a line is not written and does not touch the file. Diagnosis depends on
    // this log, so the default keeps every transition, warning and failure; only Debug is off.
    private static volatile LogLevel _minimum = LogLevel.Info;

    // A single shell process runs for the whole game-mode session and never
    // re-runs Init, so startup-only rotation let the live file grow without bound
    // (observed ~100 MB). Cap it and re-check on write, throttled to avoid a stat
    // per line. One previous file is kept, so on-disk logs stay under ~2x the cap.
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private const long RotationCheckInterval = 256 * 1024;

    // Session-local (and therefore per-user) name, matching the config lock's
    // convention. Short timeout: rotation is best-effort and must never stall a
    // log write behind another process.
    private const string RotationMutexName = @"Local\WSGM.LogRotate";
    private const int RotationMutexTimeoutMs = 1000;
    private static long _bytesSinceRotationCheck;

    // Last state written for each Change() key, with the number of identical polls suppressed
    // since. Most keys are compile-time constants, but some are per-subject (one window's tray
    // rejections, say), so this is capped: past the limit the whole map is dropped and the next
    // poll of each key writes one line again. Losing suppression is the correct failure — a
    // diagnostic must never be the thing that grows without bound.
    private const int MaxChangeKeys = 512;
    private static readonly Dictionary<string, (string Message, long Repeats)> LastByKey = [];

    /// <summary>Gets the lowest level currently reaching the file.</summary>
    public static LogLevel MinimumLevel => _minimum;

    /// <summary>Gets the per-user directory used for logs, configuration, and installed files.</summary>
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM");

    /// <summary>Initializes the named log file for the current process.</summary>
    /// <param name="name">The log file name without its extension.</param>
    public static void Init(string name = "wsgm")
    {
        try
        {
            var directory = Directory;
            System.IO.Directory.CreateDirectory(directory);
            _name = name;
            _path = Path.Combine(directory, $"{name}.log");
        }
        catch
        {
            // Logging is diagnostic only. In particular, it must not block the
            // --restore-shell recovery route when a profile is damaged.
            _path = null;
            return;
        }

        RotateIfLarge();
        Info($"---- WSGM {typeof(Log).Assembly.GetName().Version} started, args: [{Environment.CommandLine}]");
    }

    /// <summary>Sets the lowest level that reaches the file.</summary>
    /// <param name="minimum">Lowest level to record; lines below it are dropped before any I/O.</param>
    /// <remarks>
    /// Applied at startup and again whenever configuration reloads, so raising verbosity does not
    /// need a restart. Suppressed <see cref="Change"/> repeats are still counted, so a later
    /// visible line reports how long a state really held rather than only the part that was
    /// recorded.
    /// </remarks>
    public static void SetMinimumLevel(LogLevel minimum) => _minimum = minimum;

    /// <summary>Applies a configured verbosity choice.</summary>
    /// <param name="verbosity">The user's choice; verbose adds the debug level.</param>
    public static void SetVerbosity(LogVerbosity verbosity) =>
        SetMinimumLevel(verbosity == LogVerbosity.Verbose ? LogLevel.Debug : LogLevel.Info);

    /// <summary>Writes detail that only matters while investigating a specific problem.</summary>
    /// <param name="message">The message to record.</param>
    /// <remarks>
    /// Suppressed unless verbose diagnostics are on. This is the level for values that would
    /// otherwise drown the log — not a licence to write per frame, because a suppressed line still
    /// costs the call and the string that built it.
    /// </remarks>
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    /// <summary>Writes an informational diagnostic message.</summary>
    /// <param name="message">The message to record.</param>
    public static void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>Writes a warning diagnostic message.</summary>
    /// <param name="message">The message to record.</param>
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    /// <summary>Writes an error diagnostic message.</summary>
    /// <param name="message">The message to record.</param>
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>Writes an error diagnostic message with exception details.</summary>
    /// <param name="message">The context describing the failure.</param>
    /// <param name="ex">The exception to record.</param>
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex}");

    /// <summary>Observes a detached operation and records any non-cancellation failure.</summary>
    /// <param name="task">Operation whose exception must be observed.</param>
    /// <param name="operation">Diagnostic name of the operation.</param>
    internal static void Observe(Task task, string operation) => _ = ObserveAsync(task, operation);

    private static async Task ObserveAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Warn($"{operation} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records a polled state under a key, writing only when it differs from what that key last
    /// recorded.
    /// </summary>
    /// <param name="key">Stable identity of the thing being observed, e.g. "steam.cef".</param>
    /// <param name="message">The current state, written verbatim when it changed.</param>
    /// <param name="level">Level for the line when it is written.</param>
    /// <remarks>
    /// Poll loops are the reason the log stops being readable. One session measured 43,392 lines of
    /// which 22,000 were five messages a timer kept re-stating — "Steam CEF: nothing is listening on
    /// port 8080" alone appeared 8,044 times — and the overlay work being diagnosed that day was
    /// buried under it. Every repeat after the first says only "still", which the timestamps already
    /// imply.
    /// <para>
    /// Suppressed repeats are counted, not discarded: the next line that does change carries
    /// "(previous state held for N more polls)", so the log still shows that the poll kept running
    /// and for how long. A silent drop would be worse than the spam, because it turns a stalled
    /// timer and a steady state into the same log.
    /// </para>
    /// </remarks>
    public static void Change(string key, string message, LogLevel level = LogLevel.Info)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(message);

        // Held for the Write as well: Monitor is reentrant, and releasing between the decision and
        // the append would let another thread's line land between them.
        lock (Gate)
        {
            if (LastByKey.TryGetValue(key, out (string Message, long Repeats) previous)
                && string.Equals(previous.Message, message, StringComparison.Ordinal))
            {
                LastByKey[key] = (previous.Message, previous.Repeats + 1);
                return;
            }

            long held = previous.Repeats;
            if (LastByKey.Count >= MaxChangeKeys)
            {
                LastByKey.Clear();
            }

            LastByKey[key] = (message, 0);
            Write(level, held > 0
                ? $"{message} (previous state held for {held} more polls)"
                : message);
        }
    }

    /// <summary>Moves the live log aside when it passes <see cref="MaxLogBytes"/>,
    /// keeping one previous file. Rotation is best effort so an open file never blocks
    /// later appends. A named mutex and an in-lock size check serialize the shell,
    /// Settings and elevated processes that share the log.</summary>
    private static void RotateIfLarge()
    {
        var path = _path;
        if (path is null)
        {
            return;
        }
        Mutex? mutex = null;
        var owned = false;
        var lockUnavailable = false;
        try
        {
            try
            {
                mutex = new Mutex(initiallyOwned: false, RotationMutexName);
                owned = mutex.WaitOne(RotationMutexTimeoutMs);
            }
            catch (AbandonedMutexException)
            {
                // A previous holder died mid-rotation; the wait still succeeded.
                owned = true;
            }
            catch
            {
                // No cross-process lock available — fall through and rotate anyway,
                // which is no worse than the behavior this replaced.
                lockUnavailable = true;
            }

            // A TIMEOUT is the opposite case: another process holds the lock and is
            // rotating right now, so proceeding would race its Move with this
            // Delete and destroy the archive the mutex exists to protect. Rotation
            // is best-effort — leave it for the next RotationCheckInterval.
            if (!owned && !lockUnavailable)
            {
                return;
            }

            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                var old = Path.Combine(Path.GetDirectoryName(path)!, $"{_name}.old.log");
                File.Delete(old);
                File.Move(path, old);
            }
        }
        catch
        {
            // Never throw from logging; rotation retries on the next interval.
        }
        finally
        {
            if (owned)
            {
                try
                {
                    mutex!.ReleaseMutex();
                }
                catch
                {
                    // Releasing a mutex this thread no longer owns must not throw here.
                }
            }
            mutex?.Dispose();
        }
    }

    // The rendered token stays a padded five characters: the [level] column is what existing greps
    // and every log excerpt in docs\ are written against.
    private static string Token(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info ",
        LogLevel.Warn => "warn ",
        _ => "error",
    };

    private static void Write(LogLevel level, string message)
    {
        if (_path is null || level < _minimum)
        {
            return;
        }

        lock (Gate)
        {
            // Timestamp inside the lock so appended lines stay in chronological order.
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Token(level)}] {message}{Environment.NewLine}";
            // A long-lived process never re-runs Init, so rotate here too — checked
            // only every RotationCheckInterval bytes to keep this off the per-line path.
            _bytesSinceRotationCheck += line.Length;
            if (_bytesSinceRotationCheck >= RotationCheckInterval)
            {
                _bytesSinceRotationCheck = 0;
                RotateIfLarge();
            }
            // Shell and settings are separate processes sharing this file; a
            // concurrent append raises a sharing-violation IOException — retry
            // briefly instead of silently dropping the line.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.AppendAllText(_path, line);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(15);
                }
                catch
                {
                    return; // never throw from logging
                }
            }
        }
    }
}
