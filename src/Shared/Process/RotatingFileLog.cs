using System.IO;
using System.Text;
using System.Threading;

namespace WSGM;

/// <summary>Best-effort UTF-8 append log with size-based rotation.</summary>
/// <remarks>
///     Shared by the launch wrapper, the packaged-game launcher and the logon service through a linked
///     source file, so none references another. Several processes can append to one file: it is opened
///     for append with read, write and delete sharing, as wsgm.log is, so rotation can rename it under them.
///     The lock serializes only this instance; separate processes do not share a write/rotation lock. A diagnostic write
///     must never fail its caller, so every error
///     is swallowed and the line is dropped. Rotation is cosmetic: when it fails, the line is still
///     appended.
/// </remarks>
/// <param name="path">Absolute path of the log file.</param>
/// <param name="rotateAtBytes">Size at which the file is moved into the first archive slot.</param>
/// <param name="archiveSuffixes">Archive suffixes, newest first; the oldest archive is overwritten.</param>
internal sealed class RotatingFileLog(string path, long rotateAtBytes, string[] archiveSuffixes)
{
    private readonly Lock _gate = new();

    /// <summary>Appends the supplied UTF-8 text without adding a line terminator.</summary>
    /// <param name="line">Text to append, including any required line terminator.</param>
    public void Append(string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfNeeded();
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
        }
        catch
        {
            // Diagnostics cannot be written (exclusive opener, full disk, damaged profile, ...).
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
                    File.Move(source, path + archiveSuffixes[index], true);
                }
            }
        }
        catch
        {
            // Rotation is cosmetic.
        }
    }
}
