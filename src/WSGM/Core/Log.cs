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
            _path = Path.Combine(directory, $"{name}.log");
        }
        catch
        {
            // Logging is diagnostic only. In particular, it must not block the
            // --restore-shell recovery route when a profile is damaged.
            _path = null;
            return;
        }

        try
        {
            // Rotate at 2 MB, keep one previous file.
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var old = Path.Combine(Path.GetDirectoryName(_path)!, $"{name}.old.log");
                File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch (Exception ex)
        {
            // A held-open log file (viewer, AV, second WSGM process) must not
            // disable logging for the whole session — plain appends still work.
            Warn($"Log rotation failed, continuing without rotating: {ex.Message}");
        }

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
