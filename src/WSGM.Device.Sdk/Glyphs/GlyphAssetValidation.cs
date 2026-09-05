using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>Stable reason a format-specific artwork check failed.</summary>
internal enum GlyphAssetImportCode
{
    /// <summary>The media payload was malformed or did not match its declared format.</summary>
    MalformedAsset,
    /// <summary>The payload dimensions did not match its declaration.</summary>
    DimensionMismatch,
}

/// <summary>One deterministic asset-import failure.</summary>
/// <param name="Sha256">Declared asset hash.</param>
/// <param name="Code">Stable failure reason.</param>
/// <param name="Message">Sanitized human-readable detail.</param>
internal sealed record GlyphAssetImportError(string Sha256, GlyphAssetImportCode Code, string Message);

internal sealed record AssetImportResult(ImportedGlyphAsset? Asset, GlyphAssetImportError? Error)
{
    internal static AssetImportResult Success(ImportedGlyphAsset asset) => new(asset, null);

    internal static AssetImportResult Failure(
        string sha256,
        GlyphAssetImportCode code,
        string message) => new(null, new GlyphAssetImportError(sha256, code, message));
}

/// <summary>
/// Validates an SVG asset for both passthrough and WSGM's bounded path renderer.
/// </summary>
/// <remarks>
/// The original SVG bytes pass through to Steam. Import verifies UTF-8, bounded well-formed XML,
/// an <c>svg</c> root, and declared dimensions, then extracts only the paths WSGM's Avalonia
/// renderer understands. Unsupported drawing features affect that local projection without
/// rewriting or rejecting the author's document.
/// </remarks>
internal static class GlyphSvgNormalizer
{
    internal static AssetImportResult Normalize(GlyphAssetLockEntry asset, ReadOnlySpan<byte> bytes)
    {
        string source;
        try
        {
            source = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Failure(asset, "SVG is not valid UTF-8.");
        }

        XmlReaderSettings settings = new()
        {
            // Imported artwork is self-contained: external resolution and DTD processing are not
            // part of the glyph package contract.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = GlyphProfileLimits.MaxAssetBytes,
        };

        GlyphViewBox? viewBox;
        List<NormalizedGlyphPath> paths = [];
        int commandCount = 0;
        try
        {
            using StringReader text = new(source);
            using XmlReader reader = XmlReader.Create(text, settings);
            if (!MoveToRoot(reader))
            {
                return Failure(asset, "The document root must be an <svg> element.");
            }

            viewBox = ReadViewBox(reader);
            ExtractPaths(reader, paths, ref commandCount);
        }
        catch (XmlException exception)
        {
            return Failure(asset, $"SVG is not well-formed XML: {exception.Message}");
        }

        if (viewBox is null)
        {
            return Failure(asset, "SVG needs a viewBox, or a positive intrinsic width and height.");
        }

        if (asset.ViewBox is { } declaredViewBox && declaredViewBox != viewBox.Value)
        {
            return AssetImportResult.Failure(
                asset.Sha256,
                GlyphAssetImportCode.DimensionMismatch,
                $"Declared viewBox {declaredViewBox} does not match SVG viewBox {viewBox.Value}.");
        }

        if (commandCount > GlyphProfileLimits.MaxSvgCommands)
        {
            return Failure(
                asset,
                $"SVG contains more than {GlyphProfileLimits.MaxSvgCommands} path commands.");
        }

        return AssetImportResult.Success(new ImportedGlyphAsset
        {
            Lock = asset,
            Vector = new NormalizedGlyphSvg
            {
                ViewBox = viewBox.Value,
                SvgUtf8 = bytes.ToArray(),
                Paths = paths,
            },
        });
    }

    /// <summary>Presentation that applies to a path, resolved through its enclosing groups.</summary>
    private readonly record struct Presentation(
        string Fill,
        string Stroke,
        decimal StrokeWidth,
        string FillRule,
        string LineCap,
        string LineJoin);

    /// <summary>Collects the paths WSGM's own renderer can draw.</summary>
    /// <param name="reader">Reader positioned on the root element.</param>
    /// <param name="paths">Receives one entry per path found.</param>
    /// <param name="commandCount">Running total used to enforce the package command budget.</param>
    /// <remarks>
    /// Deliberately forgiving. Anything it does not understand — an element it has no renderer for,
    /// an attribute outside the handful below — is skipped rather than treated as a fault, because
    /// this exists to draw glyphs in WSGM's overlay and not to pass judgement on the artwork. Steam
    /// receives the author's bytes either way, so a drawing this cannot fully read still displays
    /// there correctly.
    /// <para>
    /// Group presentation is inherited rather than refused. The Claw's controller illustration
    /// carries its stroke on nine nested groups and would otherwise draw as a set of unstyled
    /// outlines.
    /// </para>
    /// </remarks>
    private static void ExtractPaths(
        XmlReader reader,
        List<NormalizedGlyphPath> paths,
        ref int commandCount)
    {
        Presentation root = ReadPresentation(
            reader,
            new Presentation("currentColor", "none", 0, "nonzero", "butt", "miter"));
        List<Presentation> inherited = [root];
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (inherited.Count > 1)
                {
                    inherited.RemoveAt(inherited.Count - 1);
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            Presentation current = ReadPresentation(reader, inherited[^1]);
            if (reader.LocalName == "path")
            {
                string? data = reader.GetAttribute("d");
                if (!string.IsNullOrWhiteSpace(data))
                {
                    commandCount = Math.Min(
                        GlyphProfileLimits.MaxSvgCommands + 1,
                        commandCount + CountCommands(data));
                }

                if (paths.Count < GlyphProfileLimits.MaxSvgPaths
                    && !string.IsNullOrWhiteSpace(data)
                    && data.Length <= GlyphProfileLimits.MaxPathDataLength)
                {
                    paths.Add(new NormalizedGlyphPath
                    {
                        Data = data,
                        Fill = current.Fill,
                        Stroke = current.Stroke,
                        StrokeWidth = current.StrokeWidth,
                        FillRule = current.FillRule,
                        StrokeLineCap = current.LineCap,
                        StrokeLineJoin = current.LineJoin,
                    });
                }
            }

            if (!reader.IsEmptyElement)
            {
                inherited.Add(current);
            }
        }
    }

    private static int CountCommands(string pathData)
    {
        const string Commands = "MmZzLlHhVvCcSsQqTtAa";
        int count = 0;
        foreach (char character in pathData)
        {
            if (Commands.IndexOf(character) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Overlays an element's presentation attributes onto what it inherits.</summary>
    private static Presentation ReadPresentation(XmlReader reader, Presentation inherited)
    {
        string fill = reader.GetAttribute("fill") ?? inherited.Fill;
        string stroke = reader.GetAttribute("stroke") ?? inherited.Stroke;
        string fillRule = reader.GetAttribute("fill-rule")
            ?? reader.GetAttribute("clip-rule")
            ?? inherited.FillRule;
        string lineCap = reader.GetAttribute("stroke-linecap") ?? inherited.LineCap;
        string lineJoin = reader.GetAttribute("stroke-linejoin") ?? inherited.LineJoin;
        decimal strokeWidth = TryBoundedDecimal(reader.GetAttribute("stroke-width"), out decimal width)
            ? width
            : inherited.StrokeWidth;
        return new Presentation(fill, stroke, strokeWidth, fillRule, lineCap, lineJoin);
    }

    private static bool MoveToRoot(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            return reader.LocalName == "svg" && reader.Depth == 0;
        }

        return false;
    }

    /// <summary>Reads the root's view box, falling back to its intrinsic size.</summary>
    /// <param name="reader">Reader positioned on the root element.</param>
    /// <returns>The view box, or null when neither is usable.</returns>
    /// <remarks>
    /// An absent view box is not a defect: SVG defines user space as the viewport, so the intrinsic
    /// width and height ARE the view box. Four of the twenty glyphs in the first packaged profile
    /// are exported exactly that way.
    /// </remarks>
    private static GlyphViewBox? ReadViewBox(XmlReader reader)
    {
        string? viewBoxText = reader.GetAttribute("viewBox");
        if (viewBoxText is not null && TryParseViewBox(viewBoxText, out GlyphViewBox parsed))
        {
            return parsed;
        }

        if (TryBoundedDecimal(reader.GetAttribute("width"), out decimal width)
            && TryBoundedDecimal(reader.GetAttribute("height"), out decimal height)
            && width > 0
            && height > 0)
        {
            return new GlyphViewBox(0, 0, width, height);
        }

        return null;
    }

    private static bool TryParseViewBox(string value, out GlyphViewBox viewBox)
    {
        viewBox = default;
        string[] parts = value.Split(
            [' ', ',', '\t', '\n', '\r'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !TryBoundedDecimal(parts[0], out decimal x)
            || !TryBoundedDecimal(parts[1], out decimal y)
            || !TryBoundedDecimal(parts[2], out decimal width)
            || !TryBoundedDecimal(parts[3], out decimal height)
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        viewBox = new GlyphViewBox(x, y, width, height);
        return true;
    }

    private static bool TryBoundedDecimal(string? value, out decimal parsed)
    {
        parsed = 0;
        return value is not null
            && decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out parsed)
            && Math.Abs(parsed) <= GlyphProfileLimits.MaxDimension;
    }

    private static AssetImportResult Failure(GlyphAssetLockEntry asset, string message) =>
        AssetImportResult.Failure(asset.Sha256, GlyphAssetImportCode.MalformedAsset, message);
}

internal static class GlyphPngInspector
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static AssetImportResult Inspect(GlyphAssetLockEntry asset, byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.Length < 33 || !span[..8].SequenceEqual(Signature))
        {
            return Failure(asset, "PNG signature is absent or truncated.");
        }

        int offset = 8;
        bool sawHeader = false;
        bool sawData = false;
        bool sawEnd = false;
        bool sawPalette = false;
        bool dataEnded = false;
        byte headerColorType = 0;
        byte headerBitDepth = 0;
        int width = 0;
        int height = 0;
        using MemoryStream compressedImage = new();
        while (offset < span.Length)
        {
            if (span.Length - offset < 12)
            {
                return Failure(asset, "PNG chunk header is truncated.");
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
            if (length > GlyphProfileLimits.MaxAssetBytes
                || length > (uint)(span.Length - offset - 12))
            {
                return Failure(asset, "PNG chunk length exceeds the asset bounds.");
            }

            ReadOnlySpan<byte> typeBytes = span.Slice(offset + 4, 4);
            string type = Encoding.ASCII.GetString(typeBytes);
            ReadOnlySpan<byte> data = span.Slice(offset + 8, (int)length);
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                span.Slice(offset + 8 + (int)length, 4));
            uint actualCrc = Crc32(span.Slice(offset + 4, checked((int)length + 4)));
            if (storedCrc != actualCrc)
            {
                return Failure(asset, $"PNG chunk '{type}' has an invalid CRC.");
            }
            offset += checked((int)length + 12);

            if (!sawHeader)
            {
                if (type != "IHDR" || length != 13)
                {
                    return Failure(asset, "PNG must begin with one 13-byte IHDR chunk.");
                }
                uint widthValue = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                uint heightValue = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                byte bitDepth = data[8];
                byte colorType = data[9];
                if (widthValue is 0 or > GlyphProfileLimits.MaxDimension
                    || heightValue is 0 or > GlyphProfileLimits.MaxDimension
                    || !ValidColorEncoding(bitDepth, colorType)
                    || data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    return Failure(asset, "PNG IHDR dimensions or encoding fields are unsafe.");
                }
                width = (int)widthValue;
                height = (int)heightValue;
                headerColorType = colorType;
                headerBitDepth = bitDepth;
                sawHeader = true;
                continue;
            }

            if (type is "acTL" or "fcTL" or "fdAT" or "tEXt" or "zTXt" or "iTXt")
            {
                return Failure(asset, $"PNG chunk '{type}' is not accepted for static artwork.");
            }

            if (type == "IDAT")
            {
                if (dataEnded || (headerColorType == 3 && !sawPalette))
                {
                    return Failure(asset, "PNG IDAT ordering is invalid.");
                }
                sawData = true;
                compressedImage.Write(data);
            }
            else if (type == "PLTE")
            {
                if (sawData || length is 0 or > 768 || length % 3 != 0)
                {
                    return Failure(asset, "PNG palette is malformed or appears after image data.");
                }
                sawPalette = true;
            }
            else if (type == "IEND")
            {
                if (length != 0 || offset != span.Length)
                {
                    return Failure(asset, "PNG IEND is malformed or followed by trailing bytes.");
                }
                sawEnd = true;
                break;
            }
            else if (char.IsUpper(type[0]))
            {
                return Failure(asset, $"Unsupported critical PNG chunk '{type}'.");
            }
            else if (sawData)
            {
                dataEnded = true;
            }
        }

        if (!sawHeader || !sawData || !sawEnd)
        {
            return Failure(asset, "PNG is missing IHDR, IDAT, or IEND.");
        }

        if (asset.PixelWidth != width || asset.PixelHeight != height)
        {
            return AssetImportResult.Failure(
                asset.Sha256,
                GlyphAssetImportCode.DimensionMismatch,
                "PNG dimensions do not match its declared dimensions.");
        }

        if (!ValidateDecodedRaster(
            compressedImage.ToArray(),
            width,
            height,
            headerBitDepth,
            headerColorType))
        {
            return Failure(asset, "PNG image data is malformed or exceeds its decoded bounds.");
        }

        return AssetImportResult.Success(new ImportedGlyphAsset
        {
            Lock = asset,
            RasterPng = bytes.ToArray(),
        });
    }

    private static AssetImportResult Failure(GlyphAssetLockEntry asset, string message) =>
        AssetImportResult.Failure(asset.Sha256, GlyphAssetImportCode.MalformedAsset, message);

    private static bool ValidColorEncoding(byte bitDepth, byte colorType) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        4 => bitDepth is 8 or 16,
        6 => bitDepth is 8 or 16,
        _ => false,
    };

    private static bool ValidateDecodedRaster(
        byte[] compressed,
        int width,
        int height,
        byte bitDepth,
        byte colorType)
    {
        int channels = colorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (channels == 0)
        {
            return false;
        }

        int scanlineBytes = checked((width * channels * bitDepth + 7) / 8);
        int expectedBytes = checked((scanlineBytes + 1) * height);
        if (expectedBytes > GlyphProfileLimits.MaxRasterPixels * 4 + height)
        {
            return false;
        }

        byte[] decoded = new byte[expectedBytes + 1];
        try
        {
            using MemoryStream input = new(compressed, writable: false);
            using ZLibStream inflater = new(input, CompressionMode.Decompress, leaveOpen: false);
            int received = 0;
            while (received < decoded.Length)
            {
                int read = inflater.Read(decoded, received, decoded.Length - received);
                if (read == 0)
                {
                    break;
                }
                received += read;
            }

            if (received != expectedBytes)
            {
                return false;
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        for (int row = 0; row < height; row++)
        {
            if (decoded[row * (scanlineBytes + 1)] > 4)
            {
                return false;
            }
        }
        return true;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0xedb88320u & mask);
            }
        }
        return ~crc;
    }
}
