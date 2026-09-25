using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace WSGM.DeviceLab.Wizard;

/// <summary>What a monitor's EDID says about its panel. The serial number is never read.</summary>
internal sealed record LabEdidSummary
{
    /// <summary>EDID version, for example 1.4.</summary>
    public required string Version { get; init; }

    /// <summary>Three-letter manufacturer ID.</summary>
    public required string Manufacturer { get; init; }

    /// <summary>Product code, as hex.</summary>
    public required string ProductCode { get; init; }

    /// <summary>Monitor name from the name descriptor.</summary>
    public string? Name { get; init; }

    /// <summary>Other text descriptors, for example the panel model. Serial text is left out.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];

    /// <summary>Preferred (native) width from the first detailed timing.</summary>
    public int? NativeWidth { get; init; }

    /// <summary>Preferred (native) height from the first detailed timing.</summary>
    public int? NativeHeight { get; init; }

    /// <summary>Preferred refresh rate from the first detailed timing.</summary>
    public double? NativeRefreshHz { get; init; }

    /// <summary>Whether the panel is natively portrait.</summary>
    public string? NativeOrientation { get; init; }

    /// <summary>Lowest vertical rate from the range limits descriptor.</summary>
    public int? MinimumRefreshHz { get; init; }

    /// <summary>Highest vertical rate from the range limits descriptor.</summary>
    public int? MaximumRefreshHz { get; init; }

    /// <summary>The EDID 1.4 continuous-frequency feature bit.</summary>
    public bool ContinuousFrequency { get; init; }

    /// <summary>Whether a CTA extension carries the AMD FreeSync vendor block.</summary>
    public bool FreeSyncBlock { get; init; }

    /// <summary>Whether a DisplayID extension carries an adaptive-sync block.</summary>
    public bool AdaptiveSyncBlock { get; init; }

    /// <summary>
    ///     Whether variable refresh looks supported: a FreeSync or adaptive-sync block, or a
    ///     continuous-frequency panel with a refresh range.
    /// </summary>
    public bool VariableRefreshLikely { get; init; }
}

internal static partial class LabSystemDump
{
    private static readonly byte[] EdidHeader = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>Decodes an EDID without its serial number fields.</summary>
    /// <param name="edid">Base block and any extension blocks.</param>
    /// <returns>The summary, or <see langword="null" /> when the bytes are not an EDID.</returns>
    public static LabEdidSummary? ParseEdid(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(EdidHeader))
        {
            return null;
        }

        // Bytes 12-15 (the serial number) and the 0xFF serial descriptor are never read.
        var manufacturer = BinaryPrimitives.ReadUInt16BigEndian(edid[8..]);
        Span<char> letters =
        [
            (char)('@' + ((manufacturer >> 10) & 0x1F)),
            (char)('@' + ((manufacturer >> 5) & 0x1F)),
            (char)('@' + (manufacturer & 0x1F))
        ];
        string? name = null;
        List<string> text = [];
        int? width = null;
        int? height = null;
        double? refresh = null;
        int? minimum = null;
        int? maximum = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            var descriptor = edid.Slice(offset, 18);
            if (descriptor[0] != 0 || descriptor[1] != 0)
            {
                if (width is null)
                {
                    var clock = BinaryPrimitives.ReadUInt16LittleEndian(descriptor) * 10_000.0;
                    var horizontal = descriptor[2] | ((descriptor[4] >> 4) << 8);
                    var horizontalBlank = descriptor[3] | ((descriptor[4] & 0x0F) << 8);
                    var vertical = descriptor[5] | ((descriptor[7] >> 4) << 8);
                    var verticalBlank = descriptor[6] | ((descriptor[7] & 0x0F) << 8);
                    width = horizontal;
                    height = vertical;
                    var total = (double)(horizontal + horizontalBlank) * (vertical + verticalBlank);
                    refresh = total > 0 ? Math.Round(clock / total, 2) : null;
                }

                continue;
            }

            switch (descriptor[3])
            {
                case 0xFC:
                    name = DescriptorText(descriptor);
                    break;
                case 0xFE when DescriptorText(descriptor) is { Length: > 0 } value:
                    text.Add(value);
                    break;
                case 0xFD:
                {
                    var flags = descriptor[4];
                    minimum = descriptor[5] + ((flags & 0x03) == 0x03 ? 255 : 0);
                    maximum = descriptor[6] + ((flags & 0x02) != 0 ? 255 : 0);
                    break;
                }
            }
        }

        var freeSync = false;
        var adaptiveSync = false;
        var extensions = Math.Min(edid[126], edid.Length / 128 - 1);
        for (var block = 1; block <= extensions; block++)
        {
            var extension = edid.Slice(block * 128, 128);
            if (extension[0] == 0x02)
            {
                freeSync |= HasFreeSyncBlock(extension);
            }
            else if (extension[0] == 0x70)
            {
                adaptiveSync |= HasAdaptiveSyncBlock(extension);
            }
        }

        var continuous = (edid[24] & 0x01) != 0;
        return new LabEdidSummary
        {
            Version = $"{edid[18]}.{edid[19]}",
            Manufacturer = new string(letters),
            ProductCode = Hex(BinaryPrimitives.ReadUInt16LittleEndian(edid[10..]), 4),
            Name = name,
            Text = text,
            NativeWidth = width,
            NativeHeight = height,
            NativeRefreshHz = refresh,
            NativeOrientation = width is { } w && height is { } h ? w < h ? "portrait" : "landscape" : null,
            MinimumRefreshHz = minimum,
            MaximumRefreshHz = maximum,
            ContinuousFrequency = continuous,
            FreeSyncBlock = freeSync,
            AdaptiveSyncBlock = adaptiveSync,
            VariableRefreshLikely = freeSync || adaptiveSync || (continuous && minimum < maximum)
        };
    }

    // CTA-861 data block collection: a vendor-specific block (tag 3) with AMD's OUI 00-00-1A.
    private static bool HasFreeSyncBlock(ReadOnlySpan<byte> extension)
    {
        var end = Math.Min((int)extension[2], 127);
        for (var offset = 4; offset < end;)
        {
            var tag = extension[offset] >> 5;
            var length = extension[offset] & 0x1F;
            if (tag == 3 && length >= 3 && offset + 3 < 128
                && extension[offset + 1] == 0x1A && extension[offset + 2] == 0x00 && extension[offset + 3] == 0x00)
            {
                return true;
            }

            offset += length + 1;
        }

        return false;
    }

    // DisplayID 2.x adaptive-sync data block, tag 0x2B.
    private static bool HasAdaptiveSyncBlock(ReadOnlySpan<byte> extension)
    {
        var end = Math.Min(5 + extension[2], 127);
        for (var offset = 5; offset + 2 < end;)
        {
            if (extension[offset] == 0x2B)
            {
                return true;
            }

            offset += 3 + extension[offset + 2];
        }

        return false;
    }

    private static string DescriptorText(ReadOnlySpan<byte> descriptor)
    {
        var text = Encoding.ASCII.GetString(descriptor[5..]);
        var end = text.IndexOf('\n');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    // The monitor device path is \\?\DISPLAY#<model>#<instance>#{interface GUID}; its EDID lives under
    // the device's Enum key. Only the parsed summary is kept; the raw bytes hold the serial.
    private static LabEdidSummary? EdidFromRegistry(string? monitorPath)
    {
        if (monitorPath is null || !monitorPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return null;
        }

        var trimmed = monitorPath[4..];
        var guid = trimmed.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0)
        {
            trimmed = trimmed[..guid];
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{trimmed.Replace('#', '\\')}\Device Parameters");
            return key?.GetValue("EDID") is byte[] { Length: <= 4096 } edid ? ParseEdid(edid) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or SecurityException or ArgumentException)
        {
            return null;
        }
    }
}
