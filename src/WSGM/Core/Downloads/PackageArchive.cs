using System;
using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace WSGM.Core;

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
        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.OpenRead(archivePath);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            ExtractTar(gzip, destination, cancellationToken);
            return;
        }

        if (Path.GetExtension(archivePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ExtractSelfExtracting(archivePath, destination, cancellationToken);
            return;
        }

        using var stream = File.OpenRead(archivePath);
        Extract(stream, destination, cancellationToken);
    }

    /// <summary>Extracts an archive stream with the same path, link and cancellation rules as file extraction.</summary>
    /// <param name="stream">The archive bytes; no downloaded program is executed.</param>
    /// <param name="destination">The private destination.</param>
    /// <param name="cancellationToken">Cancels between and within entries.</param>
    internal static void Extract(Stream stream, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        using var archive = ArchiveFactory.OpenArchive(stream);
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
            var attributes = entry.Attrib ?? 0;
            if (!string.IsNullOrEmpty(entry.LinkTarget) || ((attributes >> 16) & 0xf000) == 0xa000
                || (attributes & 0xf000) == 0xa000)
            {
                throw new InvalidDataException("Archive links are not permitted.");
            }

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

    private static void ExtractTar(Stream stream, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        using var tar = new TarReader(stream);
        var buffer = new byte[81920];
        while (tar.GetNextEntry() is { } entry)
        {
            token.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.ExtendedAttributes or TarEntryType.GlobalExtendedAttributes
                or TarEntryType.LongLink or TarEntryType.LongPath)
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(destination, entry.Name.Replace('/', Path.DirectorySeparatorChar)));
            if (entry.EntryType == TarEntryType.Directory && StoragePaths.Same(destination, path))
            {
                continue;
            }

            if (!StoragePaths.IsUnder(destination, path))
            {
                throw new InvalidDataException("An archive path escapes its private staging directory.");
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                throw new InvalidDataException("Archive links and special entries are not permitted.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = File.Open(path, FileMode.CreateNew, FileAccess.Write);
            var source = entry.DataStream;
            int read;
            while (source is not null && (read = source.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>Opens a ZIP embedded in a PE, without copying the verified input buffer.</summary>
    /// <param name="bytes">The verified setup bytes.</param>
    /// <returns>The archive-relative slice, excluding the PE prefix and trailing certificate.</returns>
    internal static MemoryStream OpenEmbeddedZip(byte[] bytes)
    {
        using var pe = new PEReader(new MemoryStream(bytes));
        var certificate = pe.PEHeaders.PEHeader?.CertificateTableDirectory.RelativeVirtualAddress ?? 0;
        var end = certificate > 0 ? certificate : bytes.Length;
        if (end > bytes.Length)
        {
            throw new InvalidDataException("The embedded archive's certificate offset is invalid.");
        }

        for (var index = end - 22; index >= Math.Max(0, end - 65557); index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x06054b50)
            {
                continue;
            }

            var comment = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 20, 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 12, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 16, 4));
            var start = (long)index - size - offset;
            if (index + 22 + comment <= end && start >= 0 && start <= index - 4
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)start, 4)) == 0x04034b50)
            {
                return new MemoryStream(bytes, (int)start, index + 22 + comment - (int)start, writable: false);
            }
        }

        throw new InvalidDataException("The setup has no valid embedded ZIP.");
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
