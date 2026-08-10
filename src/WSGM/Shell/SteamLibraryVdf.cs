using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace WSGM.Shell;

/// <summary>Generates the two Valve KeyValues files a Steam library needs, in
/// Steam's exact on-disk dialect (byte-verified against real libraries,
/// 2026-08-10): UTF-8 without BOM, LF-only line endings even on Windows, one TAB
/// per nesting level, TWO TABs between key and value, backslashes in paths
/// escaped as <c>\\</c>, file ends with the closing brace plus LF.
///
/// The library id ("contentid") is a random unsigned 64-bit chosen at creation —
/// Steam's own client stores it as <c>m_ulContentID</c> with no derivation from
/// path/volume/machine, accepts third-party-invented ids, and self-heals empty
/// ones. The same value goes into the card marker and the config registration.
///
/// Everything here is pure string work so the exact bytes are unit-testable;
/// file I/O lives in <see cref="SdFormatManager"/>.</summary>
public static class SteamLibraryVdf
{
    /// <summary>Generates a fresh library content id: a uniformly random integer
    /// in [1, 2^63), decimal-formatted — the value shape of every Steam-created
    /// id observed in the wild.</summary>
    /// <param name="taken">Ids already present in the config; collisions retry.</param>
    public static string GenerateContentId(IReadOnlySet<string> taken)
    {
        Span<byte> bytes = stackalloc byte[8];
        while (true)
        {
            // A uniformly random positive int64 in [1, 2^63), the value shape of
            // every Steam-created id. RandomNumberGenerator has no int64 range
            // helper, so draw 8 bytes and clear the sign bit.
            RandomNumberGenerator.Fill(bytes);
            var raw = BitConverter.ToUInt64(bytes) & 0x7FFF_FFFF_FFFF_FFFFUL;
            if (raw == 0)
            {
                continue;
            }
            var value = raw.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(value))
            {
                return value;
            }
        }
    }

    /// <summary>Escapes a Windows path for a VDF string value.</summary>
    /// <param name="path">The plain path, e.g. <c>E:\SteamLibrary</c>.</param>
    public static string EscapePath(string path) => path.Replace("\\", "\\\\");

    /// <summary>Builds the card marker — <c>&lt;X&gt;:\SteamLibrary\libraryfolder.vdf</c>.</summary>
    /// <param name="contentId">The generated library id.</param>
    /// <param name="steamExePath">The plain steam.exe path (escaped here).</param>
    public static string BuildMarker(string contentId, string steamExePath) =>
        "\"libraryfolder\"\n"
        + "{\n"
        + $"\t\"contentid\"\t\t\"{contentId}\"\n"
        + "\t\"label\"\t\t\"\"\n"
        + $"\t\"launcher\"\t\t\"{EscapePath(steamExePath)}\"\n"
        + "}\n";

    /// <summary>Builds one numbered registration block for
    /// <c>config\libraryfolders.vdf</c>, field order matching what Steam writes;
    /// <c>apps</c> stays empty for Steam to fill.</summary>
    /// <param name="index">The zero-based entry index.</param>
    /// <param name="libraryPath">The plain library path (escaped here).</param>
    /// <param name="contentId">The library id, matching the card marker.</param>
    /// <param name="totalSize">The volume size in bytes.</param>
    public static string BuildConfigEntry(
        int index, string libraryPath, string contentId, long totalSize) =>
        $"\t\"{index}\"\n"
        + "\t{\n"
        + $"\t\t\"path\"\t\t\"{EscapePath(libraryPath)}\"\n"
        + "\t\t\"label\"\t\t\"\"\n"
        + $"\t\t\"contentid\"\t\t\"{contentId}\"\n"
        + $"\t\t\"totalsize\"\t\t\"{totalSize.ToString(CultureInfo.InvariantCulture)}\"\n"
        + "\t\t\"update_clean_bytes_tally\"\t\t\"0\"\n"
        + "\t\t\"time_last_update_verified\"\t\t\"0\"\n"
        + "\t\t\"apps\"\n"
        + "\t\t{\n"
        + "\t\t}\n"
        + "\t}\n";

    /// <summary>All quoted values following a given key anywhere in the file —
    /// used for registered-path and content-id collision checks. Line-based on
    /// purpose: existing content is never reserialized, only inspected.</summary>
    /// <param name="vdf">The file text.</param>
    /// <param name="key">The bare key name, e.g. "path".</param>
    public static List<string> ValuesOf(string vdf, string key)
    {
        var results = new List<string>();
        var marker = $"\"{key}\"";
        foreach (var rawLine in vdf.Split('\n'))
        {
            var line = rawLine.TrimStart('\t', ' ');
            if (!line.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }
            var rest = line[marker.Length..].TrimStart('\t', ' ');
            if (rest.Length >= 2 && rest[0] == '"')
            {
                var end = rest.IndexOf('"', 1);
                if (end > 0)
                {
                    results.Add(rest[1..end]);
                }
            }
        }
        return results;
    }

    /// <summary>Whether the config already registers a library at this path
    /// (compared unescaped, case-insensitively).</summary>
    /// <param name="vdf">The config file text.</param>
    /// <param name="libraryPath">The plain library path.</param>
    public static bool IsRegistered(string vdf, string libraryPath)
    {
        foreach (var value in ValuesOf(vdf, "path"))
        {
            if (string.Equals(value.Replace("\\\\", "\\"), libraryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The next free top-level entry index: highest existing numbered
    /// block + 1. Line-based scan for <c>\t"N"</c> at nesting depth one.</summary>
    /// <param name="vdf">The config file text.</param>
    public static int NextIndex(string vdf)
    {
        var highest = -1;
        foreach (var rawLine in vdf.Split('\n'))
        {
            // Depth-one block headers are exactly one tab, then a quoted integer.
            if (rawLine.Length < 4 || rawLine[0] != '\t' || rawLine[1] != '"'
                || rawLine.StartsWith("\t\t", StringComparison.Ordinal))
            {
                continue;
            }
            var end = rawLine.IndexOf('"', 2);
            if (end > 2
                && int.TryParse(rawLine[2..end], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index)
                && index > highest)
            {
                highest = index;
            }
        }
        return highest + 1;
    }

    /// <summary>Splices a new registration block into
    /// <c>config\libraryfolders.vdf</c>, preserving every existing byte — the
    /// block is inserted immediately before the file's final closing brace.
    /// Returns false (with <paramref name="updated"/> = null) when the file does
    /// not look like a libraryfolders file or the path is already registered.</summary>
    /// <param name="vdf">The current file text (LF line endings).</param>
    /// <param name="libraryPath">The plain library path, e.g. <c>E:\SteamLibrary</c>.</param>
    /// <param name="contentId">The library id (must match the card marker).</param>
    /// <param name="totalSize">The volume size in bytes.</param>
    /// <param name="updated">The new file text on success.</param>
    public static bool TrySplice(
        string vdf, string libraryPath, string contentId, long totalSize,
        out string? updated)
    {
        updated = null;
        if (!vdf.StartsWith("\"libraryfolders\"\n{\n", StringComparison.Ordinal)
            || IsRegistered(vdf, libraryPath))
        {
            return false;
        }
        // The root block's closing brace is the last '}' in the file; everything
        // after it (a trailing LF, per Steam's own writes) is preserved.
        var close = vdf.LastIndexOf('}');
        if (close <= 0)
        {
            return false;
        }
        var entry = BuildConfigEntry(NextIndex(vdf), libraryPath, contentId, totalSize);
        updated = vdf[..close] + entry + vdf[close..];
        return true;
    }
}
