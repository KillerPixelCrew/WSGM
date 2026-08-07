using System.Text;

namespace WSGM.Deelevate;

internal static class DeelevateLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WSGM",
        "deelevate.log");

    internal static void Info(string message) => Write("info ", message);

    internal static void Error(string message) => Write("error", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.AppendAllText(Path, line, Encoding.UTF8);
                        return;
                    }
                    catch (IOException) when (attempt < 3)
                    {
                        Thread.Sleep(15);
                    }
                }
            }
        }
        catch
        {
            // A launch wrapper must never fail merely because diagnostics cannot
            // be written (concurrent helper, full disk, damaged profile, ...).
        }
    }
}
