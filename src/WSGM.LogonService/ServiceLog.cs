using System;
using System.IO;

namespace WSGM.LogonService;

/// <summary>File logger at %ProgramData%\WSGM\wsgm-service.log. SYSTEM must not
/// write into user directories, so the service cannot share wsgm.log — WSGM's own
/// log stays the primary diagnostic surface (it records the --boot start); this
/// file is only needed when the splash never appeared at all. Best effort: logging
/// failures never take the service down.</summary>
internal static class ServiceLog
{
    private const long RotateBytes = 1024 * 1024;
    private static readonly object Gate = new();

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WSGM");

    /// <summary>Absolute path of the service log file.</summary>
    internal static string LogPath => Path.Combine(Directory, "wsgm-service.log");

    internal static void Info(string message) => Write("INFO", message);

    internal static void Warn(string message) => Write("WARN", message);

    internal static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                try
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > RotateBytes)
                    {
                        File.Move(LogPath, LogPath + ".old", overwrite: true);
                    }
                }
                catch
                {
                    // Rotation is cosmetic.
                }
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never let logging break the service.
        }
    }
}
