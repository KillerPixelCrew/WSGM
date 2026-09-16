using System.IO;
using System.Text;
using System.Threading;

namespace WSGM;

/// <summary>Best-effort append log with size-based rotation.</summary>
/// <remarks>
/// Shared by the launch wrapper and the logon service through a linked source file, so neither
/// references the other. A diagnostic write must never fail its caller, so every error is swallowed.
/// Rotation is cosmetic: when it fails, the line is still appended.
/// </remarks>
/// <param name="path">Absolute path of the log file.</param>
/// <param name="rotateAtBytes">Size at which the file is moved into the first archive slot.</param>
/// <param name="archiveSuffixes">Archive suffixes, newest first; the oldest archive is overwritten.</param>
/// <param name="encoding">Encoding for appended text, including whether a new file gets a preamble.</param>
/// <param name="writeRetries">Retries after an <see cref="IOException"/>, for writers that share the file.</param>
internal sealed class RotatingFileLog(
    string path,
    long rotateAtBytes,
    string[] archiveSuffixes,
    Encoding encoding,
    int writeRetries)
{
    private readonly Lock _gate = new();

    /// <summary>Appends one complete line, including its line terminator.</summary>
    public void Append(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        RotateIfNeeded();
                        File.AppendAllText(path, line, encoding);
                        return;
                    }
                    catch (IOException) when (attempt < writeRetries)
                    {
                        Thread.Sleep(15);
                    }
                }
            }
        }
        catch
        {
            // Diagnostics cannot be written (concurrent writer, full disk, damaged profile, ...).
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < rotateAtBytes)
            {
                return;
            }

            for (var index = archiveSuffixes.Length - 1; index >= 0; index--)
            {
                var source = index == 0 ? path : path + archiveSuffixes[index - 1];
                if (File.Exists(source))
                {
                    File.Move(source, path + archiveSuffixes[index], overwrite: true);
                }
            }
        }
        catch
        {
            // Rotation is cosmetic.
        }
    }
}
