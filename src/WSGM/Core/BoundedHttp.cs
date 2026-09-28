using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Reads a third party's HTTP answer up to a size WSGM is willing to take.</summary>
internal static class BoundedHttp
{
    /// <summary>How long a body may send nothing before the read gives up.</summary>
    /// <remarks>
    ///     A body is read after the headers, where HttpClient's own timeout no longer applies, so a
    ///     dropped connection would otherwise hold the read, and the service's busy flag, forever.
    /// </remarks>
    internal static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Reads the whole body into memory: the declared length is checked first, then every read.</summary>
    /// <param name="content">The answer's content.</param>
    /// <param name="maximum">The most bytes accepted.</param>
    /// <param name="tooLarge">Makes the exception thrown when the body is larger.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body, positioned at its start.</returns>
    internal static async Task<MemoryStream> ReadAsync(
        HttpContent content, int maximum, Func<Exception> tooLarge, CancellationToken cancellationToken)
    {
        MemoryStream output = new();
        try
        {
            await CopyAsync(content, output, maximum, tooLarge, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        output.Position = 0;
        return output;
    }

    /// <summary>Copies the whole body to a stream: the declared length is checked first, then every read.</summary>
    /// <param name="content">The answer's content.</param>
    /// <param name="output">Where the body goes.</param>
    /// <param name="maximum">The most bytes accepted.</param>
    /// <param name="tooLarge">Makes the exception thrown when the body is larger.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <param name="stallTimeout">How long a read may wait for data, or null for <see cref="DefaultStallTimeout" />.</param>
    /// <exception cref="IOException">The body sent nothing for the stall timeout.</exception>
    internal static async Task CopyAsync(
        HttpContent content, Stream output, long maximum, Func<Exception> tooLarge,
        CancellationToken cancellationToken, TimeSpan? stallTimeout = null)
    {
        var stall = stallTimeout ?? DefaultStallTimeout;
        if (content.Headers.ContentLength is { } length && length > maximum)
        {
            throw tooLarge();
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read;
            reading.CancelAfter(stall);
            try
            {
                read = await input.ReadAsync(buffer, reading.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"The answer sent nothing for {stall.TotalSeconds:0} seconds.");
            }

            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximum)
            {
                throw tooLarge();
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
