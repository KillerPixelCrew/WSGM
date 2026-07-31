using System;
using System.IO;

namespace WSGM.Core;

/// <summary>Tiny synchronized file logger. No toasts/taskbar exist in shell mode,
/// so the log file is the primary diagnostic surface.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM");

    public static void Init(string name = "wsgm")
    {
        try
        {
            var directory = Directory;
            System.IO.Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, $"{name}.log");

            // Rotate at 2 MB, keep one previous file.
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var old = Path.Combine(directory, $"{name}.old.log");
                File.Delete(old);
                File.Move(_path, old);
            }

            Info($"---- WSGM {typeof(Log).Assembly.GetName().Version} started, args: [{Environment.CommandLine}]");
        }
        catch
        {
            // Logging is diagnostic only. In particular, it must not block the
            // --restore-shell recovery route when a profile is damaged.
            _path = null;
        }
    }

    public static void Info(string message) => Write("info ", message);
    public static void Warn(string message) => Write("warn ", message);
    public static void Error(string message) => Write("error", message);
    public static void Error(string message, Exception ex) => Write("error", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_path, line); } catch { /* never throw from logging */ }
        }
    }
}
