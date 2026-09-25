using System;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Application;

/// <summary>
///     The step log, <c>wsgm-device.log</c> beside the executable, so a tester can send it after a crash
///     and it says which step was running.
/// </summary>
/// <remarks>
///     Every line is written through to disk and flushed before the call that logs it returns, so a line
///     written before a step survives a hard reset during that step. The wizard, its elevated relaunch and
///     the hardware worker share the file: each line is one append under a named mutex, tagged with the
///     process id and role. Lines name steps, sections, services and methods, never device paths, serials
///     or user folders; the file is not part of the redacted report. Nothing high-rate is logged: no input
///     samples, rumble frames or sensor readings. When the executable's folder cannot be written, or is a
///     drive root, the log goes to the temp folder instead. A failed write never stops the tool.
/// </remarks>
internal static class LabTrace
{
    /// <summary>The log's file name.</summary>
    public const string FileName = "wsgm-device.log";

    private const long MaxBytes = 4L * 1024 * 1024;
    private const string MutexName = @"Local\WSGM.DeviceLab.Log";
    private static readonly Lock Gate = new();
    private static string _role = "cli";

    /// <summary>Where the log is written, or null before <see cref="Start" /> or when nowhere is writable.</summary>
    public static string? Path { get; private set; }

    /// <summary>Opens the log for this process and records how it started.</summary>
    /// <param name="role">What this process is, for example <c>wizard</c> or <c>worker</c>.</param>
    public static void Start(string role)
    {
        _role = role;
        Path = Choose();
        if (Path is null)
        {
            return;
        }

        RotateIfLarge(Path);
        var version = typeof(LabTrace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        Write($"start {role}: Device Lab {version}, elevated={Elevated()}, {Environment.OSVersion.VersionString}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write($"unhandled {Describe(args.ExceptionObject as Exception)}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Write($"unobserved task {Describe(args.Exception)}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write($"exit {role}");
    }

    /// <summary>Writes one line and waits until it is on disk.</summary>
    /// <param name="message">What happens next, or what just happened.</param>
    public static void Write(string message)
    {
        if (Path is not { } path)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId} {_role}] {message}"
                   + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (Gate)
        {
            try
            {
                using Mutex mutex = new(false, MutexName);
                var owned = false;
                try
                {
                    try
                    {
                        owned = mutex.WaitOne(TimeSpan.FromSeconds(1));
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                    }

                    // Without the mutex the line is still written; interleaving is better than a gap.
                    using FileStream stream = new(path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                finally
                {
                    if (owned)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or WaitHandleCannotBeOpenedException)
            {
                // The log is a diagnostic; it never takes the tool down with it.
            }
        }
    }

    /// <summary>A one-line description of an exception: its type and message, without a stack.</summary>
    /// <param name="exception">The exception, or null.</param>
    /// <returns>The description.</returns>
    public static string Describe(Exception? exception)
    {
        return exception is null ? "(no exception)" : $"{exception.GetType().Name}: {exception.Message}";
    }

    private static string? Choose()
    {
        var folder = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
        if (folder is not null && !IsRoot(folder) && CanWrite(System.IO.Path.Combine(folder, FileName)))
        {
            return System.IO.Path.Combine(folder, FileName);
        }

        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), FileName);
        return CanWrite(temp) ? temp : null;
    }

    private static bool IsRoot(string folder)
    {
        var full = System.IO.Path.GetFullPath(folder);
        return string.Equals(System.IO.Path.GetPathRoot(full)?.TrimEnd('\\'), full.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWrite(string path)
    {
        try
        {
            using FileStream _ = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // One previous file is kept, so a long session cannot grow the log without bound.
    private static void RotateIfLarge(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                File.Move(path, System.IO.Path.ChangeExtension(path, ".previous.log"), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
