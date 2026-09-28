using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Reads a third party's HTTP answer up to a size WSGM is willing to hold in memory.</summary>
internal static class BoundedHttp
{
    /// <summary>Reads the whole body: the declared length is checked first, then every read.</summary>
    /// <param name="content">The answer's content.</param>
    /// <param name="maximum">The most bytes accepted.</param>
    /// <param name="tooLarge">Makes the exception thrown when the body is larger.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The body, positioned at its start.</returns>
    internal static async Task<MemoryStream> ReadAsync(
        HttpContent content, int maximum, Func<Exception> tooLarge, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximum)
        {
            throw tooLarge();
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        MemoryStream output = new();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                output.Position = 0;
                return output;
            }

            if (output.Length + read > maximum)
            {
                output.Dispose();
                throw tooLarge();
            }

            output.Write(buffer, 0, read);
        }
    }
}
