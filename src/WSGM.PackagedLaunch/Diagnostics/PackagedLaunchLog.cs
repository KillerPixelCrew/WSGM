using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace WSGM.PackagedLaunch;

/// <summary>The launcher's rotating diagnostic log.</summary>
/// <remarks>
///     <para>
///         The same shape, size and rotation as <c>launch.log</c>, in the same directory, because a
///         maintainer reading a launch problem should not have to learn a second format. Steam starts
///         this process per game, so a transcript per launch — what the spike wrote — would
///         accumulate a file per session forever.
///     </para>
///     <para>
///         What must never appear here: environment variable values or the environment block, SDDL,
///         window titles, module lists, and token SIDs beyond a yes/no and an integrity word. Names
///         and counts are enough to diagnose a launch, and the rest is either user content or a
///         standing invitation to paste a session token into an issue.
///     </para>
/// </remarks>
internal static class PackagedLaunchLog
{
    private const long RotateAtBytes = 2 * 1024 * 1024;
    private static readonly string[] ArchiveSuffixes = [".1", ".2", ".3"];
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, string> LastByKey = new(StringComparer.Ordinal);
    private static readonly string Path = System.IO.Path.Combine(
        // wsgm-allow-live-data-path: the launcher is a WSGM component and logs beside WSGM's own
        // diagnostics, exactly as the launch wrapper does.
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM",
        "packaged-launch.log");

    private static bool _console = true;

    /// <summary>Stops writing to the console, once it has been hidden.</summary>
    internal static void SuppressConsole()
    {
        lock (Gate)
        {
            _console = false;
        }
    }

    /// <summary>Records something that happened.</summary>
    internal static void Info(string message)
    {
        Write("info ", message);
    }

    /// <summary>Records something that did not work but did not stop the session.</summary>
    internal static void Warn(string message)
    {
        Write("warn ", message);
    }

    /// <summary>Records a refusal or a failure that ends the session.</summary>
    internal static void Error(string message)
    {
        Write("error", message);
    }

    /// <summary>Records a message only when it differs from the last one under the same key.</summary>
    /// <param name="key">What is being observed.</param>
    /// <param name="message">The observation.</param>
    /// <remarks>
    ///     The supervisor watches things that mostly do not change. Writing every observation would
    ///     bury the three lines that matter under a log of a game running normally.
    /// </remarks>
    internal static void Change(string key, string message)
    {
        lock (Gate)
        {
            if (LastByKey.TryGetValue(key, out var previous) && previous == message)
            {
                return;
            }

            LastByKey[key] = message;
        }

        Write("info ", message);
    }

    private static void Write(string level, string message)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [pid {2}] {3}{4}",
            DateTime.Now,
            level,
            Environment.ProcessId,
            message,
            Environment.NewLine);

        lock (Gate)
        {
            // The file first and never gated on the console: a Steam-launched run once stalled on
            // its first console write and produced no diagnostics at all.
            Append(line);
            if (!_console)
            {
                return;
            }

            try
            {
                Console.Write(line);
            }
            catch (IOException)
            {
                _console = false;
            }
        }
    }

    private static void Append(string line)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Rotate();
            File.AppendAllText(Path, line, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Diagnostics must never fail their caller: a full disk is not a reason not to launch.
        }
    }

    private static void Rotate()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length < RotateAtBytes)
            {
                return;
            }

            for (var index = ArchiveSuffixes.Length - 1; index >= 0; index--)
            {
                var source = index == 0 ? Path : Path + ArchiveSuffixes[index - 1];
                if (File.Exists(source))
                {
                    File.Move(source, Path + ArchiveSuffixes[index], true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rotation is cosmetic; the line is still appended.
        }
    }
}
