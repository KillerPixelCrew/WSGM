using System.Text;

namespace WSGM.Explorerfy;

internal static class ExplorerfyLog
{
    private static readonly object Gate = new();

    // Primary: the shared %LOCALAPPDATA%\WSGM log dir. Fallback: next to this exe
    // (the install\bin dir, always writable by the user that runs the wrapper).
    // A single-integrity mismatch — e.g. a stale explorerfy.log created by an
    // ELEVATED run that a later MEDIUM run cannot append to — must NOT silently
    // lose diagnostics, so a failed primary write retries into the fallback.
    internal static readonly string PrimaryPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM",
        "explorerfy.log");

    internal static readonly string FallbackPath = BuildFallbackPath();

    internal static void Info(string message) => Write("info ", message);

    internal static void Error(string message) => Write("error", message);

    private static string BuildFallbackPath()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return System.IO.Path.Combine(dir, "explorerfy.log");
            }
        }
        catch
        {
            // Fall through to the temp path.
        }
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "explorerfy.log");
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            if (TryAppend(PrimaryPath, line))
            {
                return;
            }
            // Primary failed (missing dir, ACL, or an integrity label on a stale
            // file). Try the fallback so the run is never invisible.
            TryAppend(FallbackPath, line);
        }
    }

    private static bool TryAppend(string path, string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.AppendAllText(path, line, Encoding.UTF8);
                    return true;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(15);
                }
            }
        }
        catch
        {
            // A launch wrapper must never fail because diagnostics cannot be written.
            return false;
        }
    }
}
