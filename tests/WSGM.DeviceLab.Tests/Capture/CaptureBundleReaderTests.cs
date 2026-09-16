using System.IO.Compression;
using System.Text;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Inventory;

namespace WSGM.Device.Tests;

public sealed class CaptureBundleReaderTests
{
    [Fact]
    public void NullBlobHashReturnsAStructuredSchemaFailure()
    {
        var bytes = Encoding.UTF8.GetBytes("blob");
        CaptureBlobDescriptor descriptor = new()
        {
            BlobId = "blob-1",
            Path = "blobs/blob-1.bin",
            MediaType = "application/octet-stream",
            Length = bytes.Length,
            Sha256 = CaptureHashFile.Hash(bytes)
        };
        var original = Bundle();
        using MemoryStream valid = new();
        CaptureBundleWriter.Write(valid, original with
        {
            Manifest = original.Manifest with { Blobs = [descriptor] },
            Blobs = [new CaptureBlobFile { Descriptor = descriptor, Bytes = bytes }]
        });
        valid.Position = 0;
        List<(string Path, string Content)> entries = [];
        using (ZipArchive archive = new(valid, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in archive.Entries.Where(entry => entry.FullName != CaptureBundleLayout.HashesPath))
            {
                using StreamReader reader = new(entry.Open(), Encoding.UTF8);
                var content = reader.ReadToEnd();
                if (entry.FullName == CaptureBundleLayout.ManifestPath)
                {
                    content = content.Replace($"\"{descriptor.Sha256}\"", "null", StringComparison.Ordinal);
                }
                entries.Add((entry.FullName, content));
            }
        }
        var hashes = string.Join('\n', entries.Select(entry =>
            $"{CaptureHashFile.Hash(Encoding.UTF8.GetBytes(entry.Content))}  {entry.Path}")) + "\n";
        entries.Add((CaptureBundleLayout.HashesPath, hashes));
        using var malformed = Archive(entries);

        Assert.Equal(CaptureBundleReadFailure.MalformedContent, CaptureBundleReader.Read(malformed).Failure);
        Assert.Contains(CaptureSchemaValidator.Validate(original.Manifest with
        {
            Blobs = [descriptor with { Sha256 = null! }]
        }), error => error.Message.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public void WriterOutputRoundTripsThroughTheBoundedReader()
    {
        using MemoryStream archive = new();
        CaptureBundleWriter.Write(archive, Bundle());
        archive.Position = 0;

        var result = CaptureBundleReader.Read(archive);

        Assert.True(result.Succeeded);
        Assert.Equal("reader-test", result.Bundle!.Manifest.BundleId);
        Assert.NotEmpty(result.EntryHashes);
    }

    [Fact]
    public void StreamingWriterRemainsByteForByteDeterministic()
    {
        using MemoryStream first = new();
        using MemoryStream second = new();

        CaptureBundleWriter.Write(first, Bundle());
        CaptureBundleWriter.Write(second, Bundle());

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void NonSeekableInputIsRejectedWithoutReading()
    {
        using NonSeekableReadStream source = new([]);

        var result = CaptureBundleReader.Read(source);

        Assert.Equal(CaptureBundleReadFailure.Unreadable, result.Failure);
    }

    [Fact]
    public void EmptyArchiveIsRejectedBeforeContentDecoding()
    {
        using var archive = Archive([]);

        Assert.Equal(
            CaptureBundleReadFailure.UnsafeArchive,
            CaptureBundleReader.Read(archive).Failure);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("/absolute.json")]
    [InlineData("streams/../../escape.json")]
    public void TraversalAndAbsoluteEntriesAreRejected(string path)
    {
        using var archive = Archive([(path, "{}")]);

        Assert.Equal(
            CaptureBundleReadFailure.UnsafeArchive,
            CaptureBundleReader.Read(archive).Failure);
    }

    [Fact]
    public void MissingCanonicalEntryIsReportedPrecisely()
    {
        using var archive = Archive([(CaptureBundleLayout.ManifestPath, "{}")]);

        Assert.Equal(
            CaptureBundleReadFailure.MissingEntry,
            CaptureBundleReader.Read(archive).Failure);
    }

    [Fact]
    public void MalformedHashManifestIsRejectedBeforeJsonDecoding()
    {
        var entries = CanonicalJsonEntries();
        entries.Add((CaptureBundleLayout.HashesPath, "not-a-hash-manifest\n"));
        using var archive = Archive(entries);

        Assert.Equal(
            CaptureBundleReadFailure.HashMismatch,
            CaptureBundleReader.Read(archive).Failure);
    }

    [Fact]
    public void ContentHashMismatchIsRejectedBeforeJsonDecoding()
    {
        var entries = CanonicalJsonEntries();
        var hashes = string.Join(
            '\n',
            entries.Select(entry => $"{new string('0', 64)}  {entry.Path}")) + "\n";
        entries.Add((CaptureBundleLayout.HashesPath, hashes));
        using var archive = Archive(entries);

        Assert.Equal(
            CaptureBundleReadFailure.HashMismatch,
            CaptureBundleReader.Read(archive).Failure);
    }

    [Fact]
    public void HashValidMalformedJsonIsRejectedAsMalformedContent()
    {
        var entries = CanonicalJsonEntries();
        var hashes = string.Join(
            '\n',
            entries.Select(entry =>
                $"{CaptureHashFile.Hash(Encoding.UTF8.GetBytes(entry.Content))}  {entry.Path}")) + "\n";
        entries.Add((CaptureBundleLayout.HashesPath, hashes));
        using var archive = Archive(entries);

        Assert.Equal(
            CaptureBundleReadFailure.MalformedContent,
            CaptureBundleReader.Read(archive).Failure);
    }

    [Fact]
    public void PrivacyPreviewRepresentsSanitizedRootsAndEveryBlobWithoutDumpingFullBytes()
    {
        var bytes = Enumerable.Range(0, 512).Select(value => (byte)value).ToArray();
        CaptureBlobDescriptor descriptor = new()
        {
            BlobId = "blob-1",
            Path = "blobs/blob-1.bin",
            MediaType = "application/octet-stream",
            Length = bytes.Length,
            Sha256 = CaptureHashFile.Hash(bytes)
        };
        var original = Bundle();
        var bundle = original with
        {
            Manifest = original.Manifest with { Blobs = [descriptor] },
            Blobs = [new CaptureBlobFile { Descriptor = descriptor, Bytes = bytes }]
        };

        var preview = CapturePrivacyPreview.Create(bundle);

        Assert.Same(bundle.Inventory, preview.Inventory);
        var blob = Assert.Single(preview.Blobs);
        Assert.Equal(bytes.Length, blob.ByteLength);
        Assert.Equal(descriptor.Sha256, blob.Sha256);
        Assert.True(blob.PrefixTruncated);
        Assert.True(Convert.FromBase64String(blob.Base64Prefix).Length < bytes.Length);
    }

    private static List<(string Path, string Content)> CanonicalJsonEntries() =>
    [
        (CaptureBundleLayout.ManifestPath, "{"),
        (CaptureBundleLayout.RecipePath, "{"),
        (CaptureBundleLayout.InventoryPath, "{"),
        (CaptureBundleLayout.RedactionPath, "{")
    ];

    private static MemoryStream Archive(IEnumerable<(string Path, string Content)> entries)
    {
        MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false), leaveOpen: false);
                writer.Write(content);
            }
        }

        output.Position = 0;
        return output;
    }

    private static SanitizedCaptureBundle Bundle()
    {
        var timestamp = DateTimeOffset.UnixEpoch;
        return new SanitizedCaptureBundle
        {
            Manifest = new ShareableCaptureManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                BundleId = "reader-test",
                ToolVersion = "test",
                StartedAt = timestamp,
                CompletedAt = timestamp,
                QpcFrequency = 1
            },
            Recipe = new ObserveOnlyRecipe
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                RecipeId = "reader-test",
                DisplayName = "Reader test"
            },
            Inventory = new MachineInventory
            {
                SchemaVersion = 1,
                Firmware = new FirmwareInventory(),
                CapturedAt = timestamp
            },
            Redaction = new CaptureRedactionManifest
            {
                SchemaVersion = CaptureSchema.CurrentVersion,
                DefaultRedactionApplied = true
            }
        };
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
