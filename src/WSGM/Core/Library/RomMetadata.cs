using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WSGM.Core;

/// <summary>Bounded metadata reads and filename rules used by ROM discovery.</summary>
internal static class RomMetadata
{
    internal sealed record Sfo(string Title, string TitleId, string Category);

    internal static string CleanTitle(string name)
    {
        var cleaned = Regex.Replace(name, @"\s*[\[(]([^\])]+)[\])]", match =>
            Regex.IsMatch(match.Groups[1].Value, @"^(?:Disc|Disk|CD)\s*\d+\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? match.Value : "",
            RegexOptions.CultureInvariant).Trim();
        return cleaned.Length > 0 ? cleaned : name;
    }

    internal static bool IsSwitchAddOn(string relativePath)
    {
        var components = relativePath.Replace('\\', '/').Split('/');
        if (components.SkipLast(1).Any(component =>
                component.Equals("_UPDATES_DLC", StringComparison.OrdinalIgnoreCase)
                || component.Equals("updates", StringComparison.OrdinalIgnoreCase)
                || component.Equals("dlc", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // A tagged application ID ends in 000; patch IDs end in 800 and add-on IDs use
        // the add-on range. Only interpret an explicit 16-digit Switch title tag.
        var id = Regex.Match(components[^1], @"\[(010[0-9a-fA-F]{13})\]", RegexOptions.CultureInvariant);
        return id.Success && ulong.TryParse(id.Groups[1].Value, NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture, out var titleId) && (titleId & 0x1fff) != 0;
    }

    internal static Sfo? ReadSfo(string eboot)
    {
        var folder = Path.GetDirectoryName(eboot)!;
        if (Path.GetFileName(folder).Equals("USRDIR", StringComparison.OrdinalIgnoreCase))
        {
            folder = Path.GetDirectoryName(folder)!;
        }

        try
        {
            var bytes = LibraryFiles.ReadBytes(Path.Combine(folder, "PARAM.SFO"));
            if (bytes is null || bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("\0PSF"u8))
            {
                return null;
            }

            uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            var keyTable = (long)Read(8);
            var dataTable = (long)Read(12);
            var count = Read(16);
            if (count > 4096 || 20L + count * 16L > bytes.Length || keyTable >= bytes.Length
                || dataTable > bytes.Length)
            {
                return null;
            }

            var title = "";
            var titleId = "";
            var category = "";
            for (var i = 0; i < count; i++)
            {
                var entry = 20 + i * 16;
                var keyStart = keyTable + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry, 2));
                var valueStart = dataTable + Read(entry + 12);
                var length = Read(entry + 4);
                if (keyStart >= bytes.Length || valueStart + length > bytes.Length || length > 65536)
                {
                    return null;
                }

                var keyEnd = Array.IndexOf(bytes, (byte)0, (int)keyStart);
                if (keyEnd < 0 || keyEnd >= dataTable)
                {
                    return null;
                }

                var key = Encoding.UTF8.GetString(bytes, (int)keyStart, keyEnd - (int)keyStart);
                if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 2, 2)) != 0x0204)
                {
                    continue;
                }

                var value = Encoding.UTF8.GetString(bytes, (int)valueStart, (int)length).TrimEnd('\0').Trim();
                switch (key)
                {
                    case "TITLE": title = value.Replace('\n', ' ').Replace('\r', ' '); break;
                    case "TITLE_ID": titleId = value; break;
                    case "CATEGORY": category = value; break;
                }
            }

            return new Sfo(title, titleId, category);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing or unreadable optional metadata keeps the path-derived title.
            return null;
        }
    }
}
