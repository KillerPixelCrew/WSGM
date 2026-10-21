using System;
using System.IO;
using System.Threading;

namespace WSGM.Core;

/// <summary>Reads MAME's 7-Zip SFX payload as archive data; never runs the downloaded stub.</summary>
internal static class EmulatorSfxArchive
{
    internal static void Extract(string source, string destination, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        var header = new byte[(int)Math.Min(input.Length, 4 * 1024 * 1024)];
        input.ReadExactly(header);
        if (header.Length < 2 || header[0] != 'M' || header[1] != 'Z')
        {
            throw new InvalidDataException("The published self-extracting archive has no Windows executable header.");
        }

        ReadOnlySpan<byte> signature = [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c];
        var offset = header.AsSpan().IndexOf(signature);
        if (offset < 0)
        {
            throw new InvalidDataException("The published self-extracting archive contains no reviewed 7-Zip payload.");
        }

        var archive = source + ".payload-" + Guid.NewGuid().ToString("N") + ".7z";
        try
        {
            input.Position = offset;
            using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
            }

            EmulatorPackages.Extract(archive, destination, token);
        }
        finally
        {
            FileCleanup.TryDelete(archive);
        }
    }
}
