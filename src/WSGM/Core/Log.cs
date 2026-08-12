using System;
using System.IO;
using System.Threading;

namespace WSGM.Core;

/// <summary>Tiny synchronized file logger. No toasts/taskbar exist in shell mode,
/// so the log file is the primary diagnostic surface.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static string _name = "wsgm";

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

    /// <summary>Writes an informational diagnostic message.</summary>
    /// <param name="message">The message to record.</param>
    public static void Info(string message) => Write("info ", message);

    /// <summary>Writes a warning diagnostic message.</summary>
    /// <param name="message">The message to record.</param>
    public static void Warn(string message) => Write("warn ", message);

    /// <summary>Writes an error diagnostic message.</summary>
    /// <param name="message">The message to record.</param>
    public static void Error(string message) => Write("error", message);

    /// <summary>Writes an error diagnostic message with exception details.</summary>
    /// <param name="message">The context describing the failure.</param>
    /// <param name="ex">The exception to record.</param>
    public static void Error(string message, Exception ex) => Write("error", $"{message}: {ex}");

    /// <summary>Moves the live log aside when it passes <see cref="MaxLogBytes"/>,
    /// keeping one previous file. Best-effort: a held-open file just leaves rotation
    /// for the next attempt, so appends keep working either way. Callers serialize
    /// this through <see cref="Gate"/>, or run it before logging starts (Init).
    ///
    /// The shell, Settings and the elevated one-shots all append to the same file, so
    /// <see cref="Gate"/> alone is not enough: two processes that both saw an oversized
    /// file used to delete each other's archive (the second Delete removed the copy the
    /// first had just moved into place), destroying up to 5 MB of the primary remote
    /// diagnosis surface. A named mutex plus a re-check inside it makes the loser see
    /// the already-rotated small file and do nothing.</summary>
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

    private static void Write(string level, string message)
    {
        if (_path is null)
        {
            return;
        }

        lock (Gate)
        {
            // Timestamp inside the lock so appended lines stay in chronological order.
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
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
