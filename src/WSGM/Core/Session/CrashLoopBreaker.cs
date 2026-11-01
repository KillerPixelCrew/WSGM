using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WSGM.Core;

/// <summary>
///     Disarms WSGM if the shell process keeps dying at logon: 3 or more
///     shell-mode starts within 2 minutes disarm the service boot automatically
///     (boot.json disabled plus the config flag cleared), so the next sign-in is a plain
///     Explorer desktop. Dropping a legacy shell registration is a migration remnant of
///     the same disarm, not its primary action.
/// </summary>
internal static class CrashLoopBreaker
{
    private static string MarkerPath(string root)
    {
        return Path.Combine(root, "shell-starts.txt");
    }

    public static void RecordStart(string root)
    {
        try
        {
            File.AppendAllText(MarkerPath(root), DateTime.UtcNow.ToString("O") + Environment.NewLine);
        }
        catch (Exception)
        {
            // Best effort: a missing marker only weakens crash-loop detection.
        }
    }

    /// <summary>Call AFTER RecordStart so the current start counts toward the 3.</summary>
    /// <param name="root">Data directory containing the startup marker.</param>
    /// <returns>
    ///     True for at least three recorded starts in the last two minutes; false for missing or unreadable markers.
    ///     Stale entries may be pruned.
    /// </returns>
    public static bool IsLooping(string root)
    {
        try
        {
            if (!File.Exists(MarkerPath(root)))
            {
                return false;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            var all = File.ReadAllLines(MarkerPath(root))
                .Select(l =>
                    DateTime.TryParse(l, null, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                .Where(t => t != DateTime.MinValue)
                .ToArray();
            var recent = all.Count(t => t > cutoff);
            if (recent >= 3)
            {
                return true;
            }

            // Trim stale entries so the file doesn't grow forever.
            if (recent < all.Length)
            {
                File.WriteAllLines(MarkerPath(root), all.Where(t => t > cutoff).Select(t => t.ToString("O")));
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Attempts to clear the crash-loop marker after the breaker fires.
    /// </summary>
    /// <param name="root">Data directory containing the startup marker; deletion failures are ignored.</param>
    public static void Reset(string root)
    {
        // Best effort: a stale marker only makes the next loop check stricter.
        FileCleanup.TryDelete(MarkerPath(root));
    }
}
