using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WSGM.Device.Sdk.Glyphs;

/// <summary>Reparse-safe bounded reader for one already selected immutable package directory.</summary>
/// <remarks>
/// Reads deny write and delete sharing and re-check every existing path component after the file
/// handle is open. This is still a data reader only; package selection and trust remain with the
/// runtime or Device Lab caller.
/// </remarks>
public sealed class ImmutableGlyphPackageDirectorySource : IGlyphPackageSource
{
    private readonly string _root;
    private readonly string _prefix;

    /// <summary>Opens a fixed package root without creating it.</summary>
    /// <param name="packageRoot">Existing expanded package directory.</param>
    /// <exception cref="InvalidDataException">The root is absent or is a reparse point.</exception>
    public ImmutableGlyphPackageDirectorySource(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        _prefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!Directory.Exists(_root) || IsLink(_root))
        {
            throw new InvalidDataException("Glyph package root is absent or is a reparse point.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateProfileIds()
    {
        string directory = Path.Combine(_root, "glyphs", "profiles");
        if (!Directory.Exists(directory) || IsLink(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => !IsLink(path))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => !string.IsNullOrEmpty(id)
                    && id.Length <= GlyphProfileLimits.MaxIdentifierLength
                    && id.AsSpan().IndexOfAnyExcept(
                        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-") < 0)
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                // One past the limit on purpose: the importer decides what to do about an
                // over-limit package, and it can only see one if the enumeration shows it. Cutting
                // at exactly the limit here made a package of 33 or more profiles indistinguishable
                // from a conforming one, with the extras silently dropped.
                .Take(GlyphProfileLimits.MaxProfiles + 1)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public bool TryRead(string relativePath, int maximumBytes, out byte[] bytes)
    {
        bytes = [];
        if (maximumBytes <= 0 || !TryConstrain(relativePath, out string path))
        {
            return false;
        }

        try
        {
            if (!PathChainIsPlain(path))
            {
                return false;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 || stream.Length > maximumBytes || !PathChainIsPlain(path))
            {
                return false;
            }

            byte[] owned = new byte[(int)stream.Length];
            stream.ReadExactly(owned);
            if (!PathChainIsPlain(path))
            {
                return false;
            }

            bytes = owned;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryConstrain(string relativePath, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        try
        {
            string candidate = Path.GetFullPath(Path.Combine(
                _root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private bool PathChainIsPlain(string path)
    {
        if (!Directory.Exists(_root) || IsLink(_root))
        {
            return false;
        }

        string current = _root;
        foreach (string segment in Path.GetRelativePath(_root, path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return false;
            }
            if (IsLink(current))
            {
                return false;
            }
        }

        return File.Exists(path);
    }

    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.Exists && (info.LinkTarget is not null
            || (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }
}
