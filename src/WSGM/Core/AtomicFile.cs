// Shared between WSGM and WSGM.LogonService (linked beside BootManifest.cs), so it stays free
// of Log and ConfigStore and carries explicit usings.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Replaces a file only once its new content is completely written.</summary>
/// <remarks>
///     The content goes to a uniquely named sibling that is moved over the destination, so a failed
///     or interrupted write leaves the previous file intact and the temporary file is removed on every
///     path. A durable write also asks Windows to put the bytes on disk before the replace.
/// </remarks>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Replaces <paramref name="path" /> with UTF-8 text.</summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="content">The complete new content.</param>
    /// <param name="durable">Writes through and flushes to disk before the replace.</param>
    /// <param name="cleanupFailed">Told about a temporary file that could not be removed.</param>
    internal static void WriteText(
        string path,
        string content,
        bool durable,
        Action<string, Exception>? cleanupFailed = null)
    {
        Write(
            path,
            stream =>
            {
                using StreamWriter writer = new(stream, Utf8NoBom, -1, true);
                writer.Write(content);
                return true;
            },
            durable,
            cleanupFailed);
    }

    /// <summary>Replaces <paramref name="path" /> with whatever <paramref name="write" /> produces.</summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="write">Writes the content and returns whether the replace should happen.</param>
    /// <param name="durable">Writes through and flushes to disk before the replace.</param>
    /// <param name="cleanupFailed">Told about a temporary file that could not be removed.</param>
    /// <returns>Whether the file was replaced.</returns>
    internal static bool Write(
        string path,
        Func<Stream, bool> write,
        bool durable,
        Action<string, Exception>? cleanupFailed = null)
    {
        var temporary = TemporaryPath(path);
        try
        {
            bool written;
            using (var stream = Open(temporary, durable, FileOptions.None))
            {
                written = write(stream);
                if (written && durable)
                {
                    stream.Flush(true);
                }
            }

            if (written)
            {
                File.Move(temporary, path, true);
            }

            return written;
        }
        finally
        {
            TryDelete(temporary, cleanupFailed);
        }
    }

    /// <summary>Replaces <paramref name="path" /> with content written asynchronously.</summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="write">Writes the complete content.</param>
    /// <param name="durable">Opens the temporary file write-through.</param>
    /// <param name="cancellationToken">Cancels the write before the replace.</param>
    /// <returns>A task that completes once the file has been replaced.</returns>
    internal static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        bool durable,
        CancellationToken cancellationToken)
    {
        var temporary = TemporaryPath(path);
        try
        {
            await using (var stream = Open(temporary, durable, FileOptions.Asynchronous))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            TryDelete(temporary, null);
        }
    }

    private static string TemporaryPath(string path)
    {
        return $"{path}.wsgm-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
    }

    private static FileStream Open(string temporary, bool durable, FileOptions options)
    {
        return new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            durable ? options | FileOptions.WriteThrough : options);
    }

    private static void TryDelete(string temporary, Action<string, Exception>? cleanupFailed)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must never replace the write or move failure that is already propagating.
            cleanupFailed?.Invoke(temporary, ex);
        }
    }
}
