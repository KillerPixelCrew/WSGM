using System;
using System.IO;
using System.Text;

namespace WSGM.DeviceLab.Application;

/// <summary>Create-new writes that reach the disk before a workflow publishes them.</summary>
/// <remarks>
///     Output path policy stays with each caller, because the recheck has to run against the exact
///     target at the point that workflow publishes it.
/// </remarks>
internal static class DurableFile
{
    private static readonly UTF8Encoding Utf8WithoutMark = new(false);

    /// <summary>Creates a new file, lets <paramref name="write" /> fill it, and flushes it to disk.</summary>
    /// <param name="path">File that must not exist yet.</param>
    /// <param name="write">Writes the content.</param>
    /// <param name="bufferSize">Stream buffer size.</param>
    /// <param name="access">Access the writer needs, for example to seek back while archiving.</param>
    internal static void WriteNew(
        string path,
        Action<FileStream> write,
        int bufferSize = 4096,
        FileAccess access = FileAccess.Write)
    {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            access,
            FileShare.None,
            bufferSize,
            FileOptions.WriteThrough);
        write(stream);
        stream.Flush(true);
    }

    /// <summary>Creates a new UTF-8 text file without a byte-order mark and flushes it to disk.</summary>
    /// <param name="path">File that must not exist yet.</param>
    /// <param name="content">Exact text, including any trailing line terminator.</param>
    internal static void WriteNewText(string path, string content)
    {
        WriteNew(path, stream => stream.Write(Utf8WithoutMark.GetBytes(content)));
    }

    /// <summary>Builds a unique hidden staging path beside the publication target.</summary>
    /// <param name="targetPath">Final file or directory path.</param>
    /// <returns>A sibling path reserved for unpublished content.</returns>
    internal static string StagingPath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)
                        ?? throw new ArgumentException("The target path has no parent directory.", nameof(targetPath));
        return Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Removes an unpublished staging file without throwing a cleanup failure.</summary>
    /// <param name="path">Staging file created by the workflow.</param>
    /// <returns>The cleanup error, or <see langword="null" /> when cleanup succeeded.</returns>
    internal static Exception? TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>Removes an unpublished staging directory without masking the failure that abandoned it.</summary>
    /// <param name="path">Staging directory created by the failed workflow.</param>
    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // Preserve the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original failure.
        }
    }
}
