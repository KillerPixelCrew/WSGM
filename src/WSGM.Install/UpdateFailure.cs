using System;
using System.IO;

namespace WSGM.Install;

/// <summary>
///     Why the last in-app update did not install. Setup writes it when a quiet update rolls back and
///     removes it after a successful one; WSGM reads it so a refused update is not mistaken for a
///     finished one.
/// </summary>
public static class UpdateFailure
{
    /// <summary>The record, <c>%ProgramData%\WSGM\update-failed.txt</c>.</summary>
    public static string Path => System.IO.Path.Combine(InstallLayout.MachineData, "update-failed.txt");

    /// <summary>Records a failed update, ignoring filesystem I/O and access failures.</summary>
    /// <param name="version">The version that did not install.</param>
    /// <param name="reason">The failed step and its note.</param>
    public static void Write(string version, string reason)
    {
        try
        {
            Directory.CreateDirectory(InstallLayout.MachineData);
            File.WriteAllText(Path, $"WSGM {version} did not install: {reason}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The update already failed; losing its explanation must not fail anything else.
        }
    }

    /// <summary>Reads the last update failure when available.</summary>
    /// <returns>The trimmed record, or null when absent or inaccessible.</returns>
    public static string? Read()
    {
        try
        {
            return File.Exists(Path) ? File.ReadAllText(Path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the record after a successful update, ignoring filesystem I/O and access failures.</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale record only repeats an old message; it is cleared by the next successful update.
        }
    }
}
