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
    /// <summary>The SVG contained active content, an external reference, or unsupported markup.</summary>
    UnsafeSvg,
    /// <summary>The SVG path data was malformed or exceeded its complexity budget.</summary>
    UnsafeGeometry,
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

internal static class GlyphSvgNormalizer
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    internal static AssetImportResult Normalize(GlyphAssetLockEntry asset, ReadOnlySpan<byte> bytes)
    {
        string source;
        try
        {
            source = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Failure(asset, GlyphAssetImportCode.MalformedAsset, "SVG is not valid UTF-8.");
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = GlyphProfileLimits.MaxAssetBytes,
        };

        List<NormalizedGlyphPath> paths = [];
        GlyphViewBox? parsedViewBox = null;
        bool rootSeen = false;
        int openPathDepth = -1;
        try
        {
            using StringReader text = new(source);
            using XmlReader reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (openPathDepth >= 0)
                        {
                            return Failure(asset, GlyphAssetImportCode.UnsafeSvg,
                                "SVG paths cannot contain child markup.");
                        }
                        if (!rootSeen)
                        {
                            if (reader.LocalName != "svg" || reader.Depth != 0
                                || (reader.NamespaceURI.Length > 0
                                    && reader.NamespaceURI != SvgNamespace))
                            {
                                return Failure(asset, GlyphAssetImportCode.UnsafeSvg,
                                    "Root element must be SVG in the standard namespace.");
                            }

                            rootSeen = true;
                            if (!TryReadRootAttributes(
                                reader,
                                out GlyphViewBox viewBox,
                                out string? rootError))
                            {
                                return Failure(asset, GlyphAssetImportCode.UnsafeSvg, rootError!);
                            }

                            parsedViewBox = viewBox;
                            continue;
                        }

                        if (reader.NamespaceURI.Length > 0 && reader.NamespaceURI != SvgNamespace)
                        {
                            return Failure(asset, GlyphAssetImportCode.UnsafeSvg,
                                "Foreign XML namespaces are not accepted.");
                        }

                        if (reader.LocalName == "g")
                        {
                            if (reader.HasAttributes)
                            {
                                return Failure(asset, GlyphAssetImportCode.UnsafeSvg,
                                    "SVG groups may not carry attributes or transforms.");
                            }
                            continue;
                        }

                        if (reader.LocalName != "path")
                        {
                            return Failure(asset, GlyphAssetImportCode.UnsafeSvg,
                                $"SVG element '{reader.LocalName}' is not allowlisted.");
                        }

                        if (paths.Count >= GlyphProfileLimits.MaxSvgPaths)
                        {
                            return Failure(asset, GlyphAssetImportCode.UnsafeGeometry,
                                "SVG exceeds the path-count budget.");
                        }

                        if (!TryReadPath(
                            reader,
                            out NormalizedGlyphPath? path,
                            out string? pathError))
                        {
                            return Failure(asset, GlyphAssetImportCode.UnsafeGeometry, pathError!);
                        }
                        paths.Add(path!);
                        if (!reader.IsEmptyElement)
                        {
                            openPathDepth = reader.Depth;
                        }
                        break;

                    case XmlNodeType.EndElement:
                        if (openPathDepth == reader.Depth && reader.LocalName == "path")
                        {
                            openPathDepth = -1;
                        }
                        break;
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Comment:
                        break;

                    case XmlNodeType.XmlDeclaration:
                        break;

                    default:
                        return Failure(asset, GlyphAssetImportCode.UnsafeSvg,
                            $"SVG node type '{reader.NodeType}' is not accepted.");
                }
            }
        }
        catch (XmlException ex)
        {
            return Failure(asset, GlyphAssetImportCode.UnsafeSvg, ex.Message);
        }

        if (!rootSeen || parsedViewBox is null || paths.Count == 0)
        {
            return Failure(asset, GlyphAssetImportCode.MalformedAsset,
                "SVG must contain a view box and at least one path.");
        }

        if (parsedViewBox.Value != asset.ViewBox)
        {
            return Failure(asset, GlyphAssetImportCode.DimensionMismatch,
                "SVG view box does not match its declared dimensions.");
        }

        NormalizedGlyphSvg normalized = new()
        {
            ViewBox = parsedViewBox.Value,
            Paths = paths,
            CanonicalSvgUtf8 = Serialize(parsedViewBox.Value, paths),
        };
        return AssetImportResult.Success(new ImportedGlyphAsset
        {
            Lock = asset,
            Vector = normalized,
        });
    }

    private static bool TryReadRootAttributes(
        XmlReader reader,
        out GlyphViewBox viewBox,
        out string? error)
    {
        viewBox = default;
        error = null;
        string? viewBoxText = null;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.Name == "xmlns" && reader.Value == SvgNamespace)
                {
                    continue;
                }

                if (reader.LocalName == "viewBox" && reader.Prefix.Length == 0)
                {
                    viewBoxText = reader.Value;
                    continue;
                }

                error = $"SVG root attribute '{reader.Name}' is not allowlisted.";
                reader.MoveToElement();
                return false;
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        if (viewBoxText is null || !TryParseViewBox(viewBoxText, out viewBox))
        {
            error = "SVG requires a finite, bounded four-number viewBox.";
            return false;
        }

        return true;
    }

    private static bool TryReadPath(
        XmlReader reader,
        out NormalizedGlyphPath? path,
        out string? error)
    {
        path = null;
        error = null;
        string? data = null;
        string fill = "currentColor";
        string stroke = "none";
        decimal strokeWidth = 0;
        string fillRule = "nonzero";
        string lineCap = "butt";
        string lineJoin = "miter";

        if (reader.MoveToFirstAttribute())
        {
            do
            {
                string name = reader.LocalName;
                string value = reader.Value;
                if (ContainsExternalReference(value))
                {
                    error = $"SVG attribute '{name}' contains an external or active reference.";
                    reader.MoveToElement();
                    return false;
                }

                bool accepted = name switch
                {
                    "d" => Assign(ref data, value),
                    "fill" => TryColor(value, out fill),
                    "stroke" => TryColor(value, out stroke),
                    "stroke-width" => TryBoundedDecimal(value, 0, 64, out strokeWidth),
                    "fill-rule" => TryKeyword(value, "nonzero", "evenodd", out fillRule),
                    "clip-rule" => TryKeyword(value, "nonzero", "evenodd", out fillRule),
                    "stroke-linecap" => TryKeyword(value, "butt", "round", "square", out lineCap),
                    "stroke-linejoin" => TryKeyword(value, "miter", "round", "bevel", out lineJoin),
                    _ => false,
                };

                if (!accepted)
                {
                    error = $"SVG path attribute '{reader.Name}' is not allowlisted or has an invalid value.";
                    reader.MoveToElement();
                    return false;
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        if (data is null || !GlyphPathData.TryNormalize(data, out string normalized, out error))
        {
            error ??= "SVG path has no geometry.";
            return false;
        }

        path = new NormalizedGlyphPath
        {
            Data = normalized,
            Fill = fill,
            Stroke = stroke,
            StrokeWidth = strokeWidth,
            FillRule = fillRule,
            StrokeLineCap = lineCap,
            StrokeLineJoin = lineJoin,
        };
        return true;
    }

    private static byte[] Serialize(GlyphViewBox viewBox, IReadOnlyList<NormalizedGlyphPath> paths)
    {
        StringBuilder output = new();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
        }))
        {
            writer.WriteStartElement("svg", SvgNamespace);
            writer.WriteAttributeString("viewBox", FormatViewBox(viewBox));
            foreach (NormalizedGlyphPath path in paths)
            {
                writer.WriteStartElement("path", SvgNamespace);
                writer.WriteAttributeString("d", path.Data);
                writer.WriteAttributeString("fill", path.Fill);
                writer.WriteAttributeString("stroke", path.Stroke);
                if (path.StrokeWidth != 0)
                {
                    writer.WriteAttributeString("stroke-width", Format(path.StrokeWidth));
                }
                writer.WriteAttributeString("fill-rule", path.FillRule);
                writer.WriteAttributeString("stroke-linecap", path.StrokeLineCap);
                writer.WriteAttributeString("stroke-linejoin", path.StrokeLineJoin);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static bool TryParseViewBox(string value, out GlyphViewBox viewBox)
    {
        string[] parts = value.Split([' ', '\t', '\r', '\n', ','],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 4
            && TryBoundedDecimal(parts[0], -GlyphProfileLimits.MaxDimension,
                GlyphProfileLimits.MaxDimension, out decimal x)
            && TryBoundedDecimal(parts[1], -GlyphProfileLimits.MaxDimension,
                GlyphProfileLimits.MaxDimension, out decimal y)
            && TryBoundedDecimal(parts[2], 0.000001m,
                GlyphProfileLimits.MaxDimension, out decimal width)
            && TryBoundedDecimal(parts[3], 0.000001m,
                GlyphProfileLimits.MaxDimension, out decimal height))
        {
            viewBox = new GlyphViewBox(x, y, width, height);
            return true;
        }

        viewBox = default;
        return false;
    }

    private static bool TryColor(string value, out string normalized)
    {
        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "currentColor";
            return true;
        }
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "none";
            return true;
        }
        if (value.Length is 4 or 7 or 9 && value[0] == '#'
            && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
        {
            normalized = value.ToLowerInvariant();
            return true;
        }

        normalized = "";
        return false;
    }

    private static bool TryKeyword(string value, string first, string second, out string normalized)
    {
        if (value.Equals(first, StringComparison.OrdinalIgnoreCase))
        {
            normalized = first;
            return true;
        }
        if (value.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            normalized = second;
            return true;
        }
        normalized = "";
        return false;
    }

    private static bool TryKeyword(
        string value,
        string first,
        string second,
        string third,
        out string normalized)
    {
        if (TryKeyword(value, first, second, out normalized))
        {
            return true;
        }
        if (value.Equals(third, StringComparison.OrdinalIgnoreCase))
        {
            normalized = third;
            return true;
        }
        normalized = "";
        return false;
    }

    private static bool TryBoundedDecimal(
        string value,
        decimal minimum,
        decimal maximum,
        out decimal result) => decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result)
        && result >= minimum
        && result <= maximum;

    private static bool ContainsExternalReference(string value) =>
        value.Contains("url(", StringComparison.OrdinalIgnoreCase)
        || value.Contains("://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("//", StringComparison.Ordinal)
        || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);

    private static bool Assign(ref string? target, string value)
    {
        target = value;
        return true;
    }

    private static string FormatViewBox(GlyphViewBox value) =>
        $"{Format(value.X)} {Format(value.Y)} {Format(value.Width)} {Format(value.Height)}";

    private static string Format(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static AssetImportResult Failure(
        GlyphAssetLockEntry asset,
        GlyphAssetImportCode code,
        string message) => AssetImportResult.Failure(asset.Sha256, code, message);
}

internal static class GlyphPathData
{
    private const string Commands = "MmLlHhVvCcSsQqTtAaZz";

    internal static bool TryNormalize(string source, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (source.Length is 0 or > GlyphProfileLimits.MaxPathDataLength)
        {
            error = "Path data is empty or exceeds its byte budget.";
            return false;
        }

        List<PathToken> tokens = [];
        int index = 0;
        while (index < source.Length)
        {
            SkipSeparators(source, ref index);
            if (index >= source.Length)
            {
                break;
            }

            char character = source[index];
            if (char.IsAsciiLetter(character))
            {
                if (!Commands.Contains(character))
                {
                    error = $"Path command '{character}' is not supported.";
                    return false;
                }
                tokens.Add(PathToken.Command(character));
                index++;
                continue;
            }

            if (!TryReadNumber(source, ref index, out decimal number))
            {
                error = "Path data contains an invalid token.";
                return false;
            }
            tokens.Add(PathToken.Number(number));
        }

        if (tokens.Count == 0 || !tokens[0].IsCommand || tokens[0].CommandValue is not ('M' or 'm'))
        {
            error = "Path data must begin with a move command.";
            return false;
        }

        int commandCount = 0;
        int tokenIndex = 0;
        while (tokenIndex < tokens.Count)
        {
            if (!tokens[tokenIndex].IsCommand)
            {
                error = "Path numbers must follow an explicit command.";
                return false;
            }

            char command = tokens[tokenIndex++].CommandValue;
            int firstNumber = tokenIndex;
            while (tokenIndex < tokens.Count && !tokens[tokenIndex].IsCommand)
            {
                tokenIndex++;
            }

            int numberCount = tokenIndex - firstNumber;
            int arity = Arity(command);
            if (arity == 0 ? numberCount != 0 : numberCount == 0 || numberCount % arity != 0)
            {
                error = $"Path command '{command}' has the wrong number of parameters.";
                return false;
            }

            int groups = arity == 0 ? 1 : numberCount / arity;
            commandCount += groups;
            if (commandCount > GlyphProfileLimits.MaxSvgCommands)
            {
                error = "Path data exceeds the command-count budget.";
                return false;
            }

            if (command is 'A' or 'a')
            {
                for (int group = 0; group < groups; group++)
                {
                    decimal largeArc = tokens[firstNumber + group * 7 + 3].NumberValue;
                    decimal sweep = tokens[firstNumber + group * 7 + 4].NumberValue;
                    if ((largeArc is not 0 and not 1) || (sweep is not 0 and not 1))
                    {
                        error = "Arc flags must be zero or one.";
                        return false;
                    }
                }
            }
        }

        StringBuilder output = new();
        foreach (PathToken token in tokens)
        {
            if (output.Length > 0)
            {
                output.Append(' ');
            }
            output.Append(token.IsCommand
                ? token.CommandValue
                : token.NumberValue.ToString("G29", CultureInfo.InvariantCulture));
        }
        normalized = output.ToString();
        return true;
    }

    private static int Arity(char command) => char.ToUpperInvariant(command) switch
    {
        'M' or 'L' or 'T' => 2,
        'H' or 'V' => 1,
        'C' => 6,
        'S' or 'Q' => 4,
        'A' => 7,
        'Z' => 0,
        _ => -1,
    };

    private static bool TryReadNumber(string source, ref int index, out decimal number)
    {
        int start = index;
        if (index < source.Length && source[index] is '+' or '-')
        {
            index++;
        }

        bool digits = false;
        while (index < source.Length && char.IsAsciiDigit(source[index]))
        {
            digits = true;
            index++;
        }

        if (index < source.Length && source[index] == '.')
        {
            index++;
            while (index < source.Length && char.IsAsciiDigit(source[index]))
            {
                digits = true;
                index++;
            }
        }

        if (!digits)
        {
            number = 0;
            return false;
        }

        if (index < source.Length && source[index] is 'e' or 'E')
        {
            index++;
            if (index < source.Length && source[index] is '+' or '-')
            {
                index++;
            }
            int exponentStart = index;
            while (index < source.Length && char.IsAsciiDigit(source[index]))
            {
                index++;
            }
            if (index == exponentStart)
            {
                number = 0;
                return false;
            }
        }

        return decimal.TryParse(
            source.AsSpan(start, index - start),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number)
            && number is >= -1_000_000m and <= 1_000_000m;
    }

    private static void SkipSeparators(string source, ref int index)
    {
        while (index < source.Length && (char.IsWhiteSpace(source[index]) || source[index] == ','))
        {
            index++;
        }
    }

    private readonly record struct PathToken(bool IsCommand, char CommandValue, decimal NumberValue)
    {
        internal static PathToken Command(char value) => new(true, value, 0);
        internal static PathToken Number(decimal value) => new(false, '\0', value);
    }
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
