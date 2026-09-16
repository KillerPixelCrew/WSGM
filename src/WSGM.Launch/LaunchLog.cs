using System;
using System.IO;
using System.Text;

namespace WSGM.Launch;

internal static class LaunchLog
{
    // A launch wrapper must never fail merely because diagnostics cannot be written. Several wrappers
    // can share the file, so a write retries briefly on a sharing violation.
    private static readonly RotatingFileLog Log = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WSGM",
            "launch.log"),
        2 * 1024 * 1024,
        [".1", ".2", ".3"],
        Encoding.UTF8,
        3);

    internal static void Info(string message)
    {
        Write("info ", message);
    }

    internal static void Warn(string message)
    {
        Write("warn ", message);
    }

    internal static void Error(string message)
    {
        Write("error", message);
    }

    private static void Write(string level, string message)
    {
        Log.Append(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
    }
}
