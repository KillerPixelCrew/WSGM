using System;
using System.IO;
using WSGM.Install;

namespace WSGM.Setup.Engine;

/// <summary>Setup's log, <c>%ProgramData%\WSGM\setup.log</c>. Never throws.</summary>
internal static class SetupLog
{
    private static readonly object Gate = new();

    /// <summary>The log file.</summary>
    public static string Path => System.IO.Path.Combine(InstallLayout.MachineData, "setup.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(InstallLayout.MachineData);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A setup that cannot log must still install.
        }
    }
}
