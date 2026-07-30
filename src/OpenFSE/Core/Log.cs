using System;
using System.IO;

namespace OpenFSE.Core;

/// <summary>Tiny synchronized file logger. No toasts/taskbar exist in shell mode,
/// so the log file is the primary diagnostic surface.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenFSE");

    public static void Init(string name = "openfse")
    {
        System.IO.Directory.CreateDirectory(Directory);
        _path = Path.Combine(Directory, $"{name}.log");
        try
        {
            // Rotate at 2 MB, keep one previous file.
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var old = Path.Combine(Directory, $"{name}.old.log");
                File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch { /* rotation is best-effort */ }
        Info($"---- OpenFSE {typeof(Log).Assembly.GetName().Version} started, args: [{Environment.CommandLine}]");
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
