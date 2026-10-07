using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Shared streamed download/checksum/atomic promotion used by owned package updaters.</summary>
internal static class VerifiedDownload
{
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
