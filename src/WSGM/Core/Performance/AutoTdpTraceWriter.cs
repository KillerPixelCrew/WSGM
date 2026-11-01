using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WSGM.Core;

/// <summary>Appends AutoTDP trace rows to one CSV file.</summary>
/// <remarks>
///     A diagnostic, never a dependency of control. The first I/O failure closes the file and is logged
///     once; AutoTDP carries on without it. This writer performs synchronous buffered I/O;
///     the recorder owns the queue that keeps it off the controller's thread.
/// </remarks>
internal sealed class AutoTdpTraceWriter : IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    private AutoTdpTraceWriter(StreamWriter writer, string path)
    {
        _writer = writer;
        Path = path;
    }

    /// <summary>The file being written.</summary>
    internal string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            Close();
        }
    }

    /// <summary>Creates a new trace file.</summary>
    /// <param name="directory">The folder traces are kept in.</param>
    /// <param name="traceId">The session identifier used in the file name.</param>
    /// <param name="now">The session start time.</param>
    /// <returns>The writer, or null on an I/O or access failure; invalid path arguments can throw.</returns>
    internal static AutoTdpTraceWriter? Create(string directory, string traceId, DateTimeOffset now)
    {
        var path = System.IO.Path.Combine(
            directory,
            $"autotdp-{now.ToLocalTime():yyyyMMdd-HHmmss}-{traceId}.csv");
        try
        {
            Directory.CreateDirectory(directory);
            StreamWriter writer = new(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false),
                64 * 1024);
            writer.Write(AutoTdpTraceCsv.Header);
            writer.Write("\r\n");
            writer.Flush();
            return new AutoTdpTraceWriter(writer, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"AutoTDP trace could not be created at {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Appends one row.</summary>
    /// <param name="line">The formatted row.</param>
    internal void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Write(line);
                _writer.Write("\r\n");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>Flushes the managed writer and underlying stream; does not request a durable disk flush.</summary>
    internal void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Fail(ex);
            }
        }
    }

    private void Fail(Exception ex)
    {
        Log.Warn($"AutoTDP trace stopped writing {Path}: {ex.Message}");
        Close();
    }

    private void Close()
    {
        var writer = _writer;
        _writer = null;
        try
        {
            writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Warn($"AutoTDP trace did not close cleanly: {ex.Message}");
        }
    }
}
