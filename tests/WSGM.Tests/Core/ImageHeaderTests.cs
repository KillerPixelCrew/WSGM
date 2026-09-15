using System.Buffers.Binary;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class ImageHeaderTests : IDisposable
{
    private readonly string _root = System.IO.Directory.CreateTempSubdirectory("wsgm-image-header-").FullName;

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>PNG signature + a minimal IHDR chunk (length, type, big-endian
    /// width/height); the rest of the chunk is irrelevant to the header read.</summary>
    private static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[8 + 4 + 4 + 8 + 5];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    /// <summary>SOI, an APP0 segment to be skipped, then a SOFn frame header
    /// (height before width).</summary>
    private static byte[] Jpeg(ushort width, ushort height, byte sofMarker = 0xC0, bool withApp0 = true)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (withApp0)
        {
            bytes.AddRange([0xFF, 0xE0, 0x00, 0x06, 1, 2, 3, 4]);
        }
        bytes.AddRange([0xFF, sofMarker, 0x00, 0x11, 0x08]);
        bytes.AddRange([(byte)(height >> 8), (byte)(height & 0xFF)]);
        bytes.AddRange([(byte)(width >> 8), (byte)(width & 0xFF)]);
        bytes.AddRange([0x03, 0xFF, 0xD9]);
        return [.. bytes];
    }

    /// <summary>'BM' file header + a BITMAPINFOHEADER carrying signed width/height
    /// (negative height = top-down bitmap).</summary>
    private static byte[] BmpInfoHeader(int width, int height)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22, 4), height);
        return bytes;
    }

    [Fact]
    public void ReadsPngDimensionsFromIhdr()
    {
        Assert.True(ImageHeader.TryReadSize(Write("a.png", Png(1920, 1080)), out var width, out var height));
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void ReadsJpegDimensionsFromTheFirstFrameHeaderSkippingEarlierSegments()
    {
        Assert.True(ImageHeader.TryReadSize(Write("a.jpg", Jpeg(800, 600)), out var width, out var height));
        Assert.Equal(800, width);
        Assert.Equal(600, height);
    }

    [Theory]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)] // progressive
    [InlineData((byte)0xCF)]
    public void ReadsJpegDimensionsFromEveryFrameHeaderVariant(byte sofMarker)
    {
        var path = Write($"sof{sofMarker:X2}.jpg", Jpeg(640, 480, sofMarker));

        Assert.True(ImageHeader.TryReadSize(path, out var width, out var height));
        Assert.Equal(640, width);
        Assert.Equal(480, height);
    }

    [Theory]
    [InlineData((byte)0xC4)] // DHT
    [InlineData((byte)0xC8)] // JPG (reserved)
    [InlineData((byte)0xCC)] // DAC
    public void DoesNotMistakeHuffmanOrArithmeticTablesForAFrameHeader(byte marker)
    {
        var path = Write($"table{marker:X2}.jpg", Jpeg(640, 480, marker));

        Assert.False(ImageHeader.TryReadSize(path, out var width, out var height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void ReadsBottomUpBmpDimensions()
    {
        Assert.True(ImageHeader.TryReadSize(Write("up.bmp", BmpInfoHeader(320, 200)), out var width, out var height));
        Assert.Equal(320, width);
        Assert.Equal(200, height);
    }

    [Fact]
    public void ReportsTopDownBmpNegativeHeightAsAPositiveHeight()
    {
        var path = Write("down.bmp", BmpInfoHeader(320, -200));

        Assert.True(ImageHeader.TryReadSize(path, out var width, out var height));
        Assert.Equal(320, width);
        Assert.Equal(200, height);
    }

    [Fact]
    public void ReadsLegacyBitmapCoreHeaderDimensions()
    {
        var bytes = new byte[26];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18, 2), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 48);

        Assert.True(ImageHeader.TryReadSize(Write("core.bmp", bytes), out var width, out var height));
        Assert.Equal(64, width);
        Assert.Equal(48, height);
    }

    [Fact]
    public void ReportsUnknownForATextFile()
    {
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "this is not an image, it only pretends to be one");

        Assert.False(ImageHeader.TryReadSize(path, out var width, out var height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void ReportsUnknownForATruncatedPngHeader()
    {
        var truncated = Png(1920, 1080)[..14];

        Assert.False(ImageHeader.TryReadSize(Write("cut.png", truncated), out var width, out var height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void ReportsUnknownForATruncatedBmpHeader()
    {
        var truncated = BmpInfoHeader(320, 200)[..20];

        Assert.False(ImageHeader.TryReadSize(Write("cut.bmp", truncated), out _, out _));
    }

    [Fact]
    public void ReportsUnknownForAJpegThatEndsBeforeItsFrameHeader()
    {
        Assert.False(ImageHeader.TryReadSize(Write("cut.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0x00]), out _, out _));
    }

    [Fact]
    public void ReportsUnknownForAJpegWhoseScanStartsWithoutAFrameHeader()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0x33 };

        Assert.False(ImageHeader.TryReadSize(Write("scan.jpg", bytes), out _, out _));
    }

    [Fact]
    public void ReportsUnknownForAnEmptyFile()
    {
        Assert.False(ImageHeader.TryReadSize(Write("empty.png", []), out var width, out var height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void ReportsUnknownForAMissingFileWithoutThrowing()
    {
        Assert.False(ImageHeader.TryReadSize(Path.Combine(_root, "nope.png"), out _, out _));
    }

    [Fact]
    public void ReportsUnknownForZeroSizedDeclarations()
    {
        Assert.False(ImageHeader.TryReadSize(Write("zero.png", Png(0, 100)), out _, out _));
        Assert.False(ImageHeader.TryReadSize(Write("zero.bmp", BmpInfoHeader(100, 0)), out _, out _));
    }

    [Fact]
    public void ReadsTheDeclaredSizeOfAPixelBombSoCallersCanRejectIt()
    {
        // A ~40 byte file claiming 60000x60000 (3.6 gigapixels) is exactly what the
        // header read exists for: the size is readable, the limits refuse it.
        Assert.True(ImageHeader.TryReadSize(Write("bomb.png", Png(60000, 60000)), out var width, out var height));
        Assert.Equal(60000, width);
        Assert.Equal(60000, height);
        Assert.False(ImageHeader.IsWithinLimits(width, height));
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(20000, 4000, true)] // exactly the per-side limit, 80 MP total
    [InlineData(20001, 10, false)] // over the per-side limit
    [InlineData(10, 20001, false)]
    [InlineData(20000, 4001, false)] // within the per-side limit, over 80 MP
    [InlineData(0, 100, false)]
    [InlineData(100, -1, false)]
    public void IsWithinLimitsGuardsBothPerSideAndTotalPixelCounts(int width, int height, bool expected) =>
        Assert.Equal(expected, ImageHeader.IsWithinLimits(width, height));
}
