using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Wsgm.UwpSpike;

/// Console plus file transcript. The file is the artifact that gets attached to the
/// issue, so every line carries an absolute timestamp and the writing process id.
internal sealed class SpikeLog : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter? writer;

    internal SpikeLog(string path)
    {
        Path = path;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open the transcript at {path}: {ex.Message}");
        }
    }

    internal string Path { get; }

    internal void Line(string message) => Write(" ", message);

    internal void Info(string message) => Write("info", message);

    internal void Warn(string message) => Write("warn", message);

    internal void Error(string message) => Write("err ", message);

    /// A blank separator plus a heading, so the transcript reads as sections.
    internal void Section(string title)
    {
        Write(" ", string.Empty);
        Write(" ", "=== " + title + " ===");
    }

    private void Write(string level, string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = message.Length == 0 ? string.Empty : $"{stamp} [{level}] {message}";
        lock (gate)
        {
            Console.WriteLine(line);
            writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer?.Dispose();
        }
    }
}
