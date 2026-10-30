using System;
using System.IO;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace WSGM.Core.Downloads;

/// <summary>Extracts a downloaded package into a private folder without trusting its entry names.</summary>
/// <remarks>
///     Every entry is resolved under the destination and an entry that would land outside it fails the whole
///     extraction. A <c>.exe</c> is never run: it is read as a 7-Zip self-extracting archive and only its 7-Zip
///     payload is extracted.
/// </remarks>
internal static class PackageArchive
{
    private const int SelfExtractingHeaderLimit = 4 * 1024 * 1024;

    /// <summary>Extracts a <c>.zip</c>, <c>.7z</c> or 7-Zip self-extracting <c>.exe</c>.</summary>
    /// <param name="archivePath">The verified archive.</param>
    /// <param name="destination">The private folder to extract into; created when missing.</param>
    /// <param name="cancellationToken">Cancels between and within entries.</param>
    /// <exception cref="InvalidDataException">
    ///     An entry escapes the destination, or a <c>.exe</c> carries no Windows header or 7-Zip payload.
    /// </exception>
    internal static void Extract(string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (Path.GetExtension(archivePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ExtractSelfExtracting(archivePath, destination, cancellationToken);
            return;
        }

        Directory.CreateDirectory(destination);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var buffer = new byte[81920];
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                CopyEntry(reader.Entry, reader.OpenEntryStream);
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                CopyEntry(entry, entry.OpenEntryStream);
            }
        }

        return;

        void CopyEntry(IEntry entry, Func<Stream> open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Key?.Replace('/', Path.DirectorySeparatorChar) ?? "";
            var target = Path.GetFullPath(Path.Combine(destination, name));
            if (!StoragePaths.IsUnder(destination, target))
            {
                throw new InvalidDataException("An archive path escapes its private staging directory.");
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
    }

    // Reads the 7-Zip payload of a self-extracting archive as data; the downloaded stub never runs.
    private static void ExtractSelfExtracting(string source, string destination, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        var header = new byte[(int)Math.Min(input.Length, SelfExtractingHeaderLimit)];
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

            Extract(archive, destination, token);
        }
        finally
        {
            FileCleanup.TryDelete(archive);
        }
    }
}
