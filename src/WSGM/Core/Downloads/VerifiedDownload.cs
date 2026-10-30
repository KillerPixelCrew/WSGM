using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core.Downloads;

/// <summary>
///     Streams a download to a private partial file through <see cref="BoundedHttp" />, checks its size and SHA-256,
///     and only then moves it into place, so the destination is either absent or the verified file.
/// </summary>
internal static class VerifiedDownload
{
    /// <summary>Downloads, verifies and atomically places one file.</summary>
    /// <param name="open">Opens the response; it is disposed after the body is copied.</param>
    /// <param name="destination">The final path, replaced only by a verified file.</param>
    /// <param name="expectedSha256">The required SHA-256, or an empty string to only report the hash.</param>
    /// <param name="expectedSize">The required size in bytes, or zero when unknown.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <param name="copied">Receives the bytes copied so far.</param>
    /// <param name="stallTimeout">Fails a transfer that stops delivering data for this long.</param>
    /// <returns>The lowercase SHA-256 of the placed file.</returns>
    /// <exception cref="InvalidDataException">The size or SHA-256 does not match; nothing is placed.</exception>
    internal static async Task<string> WriteAsync(Func<CancellationToken, Task<HttpResponseMessage>> open,
        string destination, string expectedSha256, long expectedSize, CancellationToken cancellationToken,
        Action<long>? copied = null, TimeSpan? stallTimeout = null)
    {
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var response = await open(cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                await BoundedHttp.CopyAsync(response.Content, output, long.MaxValue,
                    static () => new InvalidDataException("The download cannot be represented by the stream."),
                    cancellationToken, stallTimeout, copied).ConfigureAwait(false);
                if (expectedSize > 0 && output.Length != expectedSize)
                {
                    throw new InvalidDataException("The downloaded size does not match its release metadata.");
                }
            }

            await using var input = File.OpenRead(partial);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken)
                .ConfigureAwait(false));
            if (expectedSha256.Length > 0 && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded file does not match its expected SHA-256.");
            }

            input.Close();
            File.Move(partial, destination, true);
            return hash;
        }
        finally
        {
            FileCleanup.TryDelete(partial);
        }
    }
}
