using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>The versioned local screensaver-pack manifest.</summary>
internal sealed class ScreensaverManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public string Type { get; set; } = "";
    public string MinimumWsgm { get; set; } = "2.3.2";
    public string? Entry { get; set; }
    public string? Folder { get; set; }
    public string? Preview { get; set; }
    public List<string> Assets { get; set; } = [];
}

/// <summary>Only trusted host code reads this packet. No packet endpoint exists in scene JavaScript.</summary>
internal sealed class ScreensaverPlayback
{
    public string Kind { get; set; } = "play";
    public string Root { get; set; } = "";
    public string Type { get; set; } = "images";
    public string Entry { get; set; } = "";
    public List<string> Assets { get; set; } = [];
    public List<string> Images { get; set; } = [];
    public int IntervalSeconds { get; set; } = 15;
    public bool Shuffle { get; set; }
    public bool Fill { get; set; }
    public int FramesPerSecond { get; set; } = 30;
    public string Revision { get; set; } = "";
    public string? Error { get; set; }
    public int ProcessId { get; set; }
    public long WindowHandle { get; set; }
}

internal sealed record ScreensaverContentResult(ScreensaverPlayback? Playback, string Type, string Detail)
{
    internal bool Available => Playback is not null;
    internal string? Preview { get; init; }
    internal string PreviewRevision { get; init; } = "";
}

/// <summary>Bounded local discovery and the same no-follow asset boundary used by the scene process.</summary>
internal static class ScreensaverContent
{
    internal const long MaximumAssetBytes = 16 * 1024 * 1024;
    internal const int MaximumImages = 2048;
    internal const int MaximumAssets = 256;
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
    };
    private static readonly HashSet<string> SceneTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".json", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".woff", ".woff2"
    };

    internal static string SelectorId(string name)
    {
        return "uioverride-" + Uri.EscapeDataString("WSGM: " + name);
    }

    internal static bool IsImage(string path)
    {
        return ImageTypes.Contains(Path.GetExtension(path));
    }

    internal static ScreensaverContentResult Read(ScreensaverSourceConfig source, Version currentVersion)
    {
        try
        {
            if (!Path.IsPathFullyQualified(source.Folder) || source.Folder.StartsWith("\\\\", StringComparison.Ordinal)
                || new DriveInfo(Path.GetPathRoot(source.Folder)!).DriveType == DriveType.Network)
            {
                throw new InvalidDataException("Choose an absolute folder on a local or removable drive.");
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Folder));
            if (!Path.IsPathFullyQualified(source.Folder) || !Directory.Exists(root))
            {
                return new ScreensaverContentResult(null, source.Pack ? "pack" : "images",
                    "Folder unavailable. Steam's stock screensaver remains the fallback.");
            }

            RequireNoLinks(root);
            string? previewPath = null;
            var playback = new ScreensaverPlayback
            {
                Root = root,
                IntervalSeconds = Math.Clamp(source.IntervalSeconds, 2, 3600),
                Shuffle = source.Shuffle,
                Fill = source.Fill,
                FramesPerSecond = Math.Clamp(source.FramesPerSecond, 1, 60)
            };
            if (source.Pack)
            {
                var manifestPath = AssetPath(root, "screensaver.json");
                if (new FileInfo(manifestPath).Length > 64 * 1024)
                {
                    throw new InvalidDataException("The pack manifest is too large.");
                }

                var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath, Encoding.UTF8),
                                   ScreensaverJson.Default.ScreensaverManifest)
                               ?? throw new InvalidDataException("The pack manifest is empty.");
                if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Id)
                    || manifest.Id.Length > 64 || manifest.Id.Any(character => !char.IsAsciiLetterOrDigit(character)
                        && character is not '-' and not '_' and not '.')
                    || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128
                    || string.IsNullOrWhiteSpace(manifest.Author) || manifest.Author.Length > 128
                    || !Version.TryParse(manifest.Version, out _)
                    || !Version.TryParse(manifest.MinimumWsgm, out var minimum) || minimum > currentVersion
                    || manifest.Type is not "images" and not "scene" || manifest.Assets is null
                    || manifest.Assets.Count > MaximumAssets)
                {
                    throw new InvalidDataException("Unsupported pack manifest or WSGM compatibility version.");
                }

                playback.Type = manifest.Type;
                if (manifest.Type == "scene")
                {
                    if (manifest.Entry is null || !new[] { ".html", ".htm" }.Contains(
                            Path.GetExtension(manifest.Entry), StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("A scene needs an HTML entry file.");
                    }

                    playback.Entry = NormalizeRelative(manifest.Entry);
                    playback.Assets = manifest.Assets.Select(NormalizeRelative)
                        .Append(playback.Entry).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (playback.Assets.Count > MaximumAssets)
                    {
                        throw new InvalidDataException("The scene exceeds the 256 asset limit.");
                    }
                    long total = 0;
                    foreach (var asset in playback.Assets)
                    {
                        if (!SceneTypes.Contains(Path.GetExtension(asset)))
                        {
                            throw new InvalidDataException("A scene declares an unsupported asset type.");
                        }

                        total += CheckFile(AssetPath(root, asset));
                    }

                    if (total > 64 * 1024 * 1024)
                    {
                        throw new InvalidDataException("The scene exceeds the 64 MiB packaged-asset budget.");
                    }
                }
                else
                {
                    var relativeFolder = manifest.Folder ?? ".";
                    root = relativeFolder == "." ? root : AssetPath(root, relativeFolder, directory: true);
                    playback.Root = root;
                }

                if (manifest.Preview is { Length: > 0 } preview)
                {
                    if (!IsImage(preview))
                    {
                        throw new InvalidDataException("Pack previews must be raster images.");
                    }

                    CheckFile(AssetPath(playback.Type == "scene" ? playback.Root :
                        Path.GetFullPath(source.Folder), preview));
                    previewPath = AssetPath(Path.GetFullPath(source.Folder), preview);
                }
            }

            if (playback.Type == "images")
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Take(4097).ToArray();
                if (files.Length > 4096)
                {
                    throw new InvalidDataException("Choose a source folder with at most 4096 files.");
                }

                playback.Images = files.Where(IsImage).Take(MaximumImages + 1).Select(path => Path.GetFileName(path))
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (playback.Images.Count > MaximumImages)
                {
                    throw new InvalidDataException($"Choose a folder with at most {MaximumImages} images.");
                }

                playback.Images.RemoveAll(image => !ReadableImage(root, image));
                if (playback.Images.Count == 0)
                {
                    throw new InvalidDataException("No readable PNG, JPEG, WebP, GIF or BMP images in this folder.");
                }

                playback.Assets = playback.Images;
                previewPath ??= AssetPath(playback.Root, playback.Images[0]);
            }

            var identity = string.Join('\n', playback.Assets.Select(asset =>
            {
                var info = new FileInfo(AssetPath(playback.Root, asset));
                return $"{asset}\t{info.Length}\t{info.LastWriteTimeUtc.Ticks}";
            }));
            playback.Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var previewInfo = previewPath is null ? null : new FileInfo(previewPath);
            return new ScreensaverContentResult(playback, playback.Type,
                playback.Type == "images" ? $"{playback.Images.Count} {(playback.Images.Count == 1 ? "image" : "images")}" :
                    $"Sandboxed scene; {playback.Assets.Count} packaged assets, {playback.FramesPerSecond} fps limit")
            {
                Preview = previewPath,
                PreviewRevision = previewInfo is null ? "" : Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{previewPath}|{previewInfo.Length}|{previewInfo.LastWriteTimeUtc.Ticks}")))
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                                      or JsonException or NotSupportedException)
        {
            return new ScreensaverContentResult(null, source.Pack ? "pack" : "images", error.Message);
        }
    }

    internal static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Contains('%') || relative.Contains('\0')
            || relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("Assets must use contained relative paths without links or traversal.");
        }

        return relative.Replace('\\', '/');
    }

    internal static string AssetPath(string root, string relative, bool directory = false)
    {
        var normalized = NormalizeRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Asset escaped its source folder.");
        }

        RequireNoLinks(path);
        if (directory ? !Directory.Exists(path) : !File.Exists(path))
        {
            throw new FileNotFoundException("A declared asset is missing.", normalized);
        }

        return path;
    }

    internal static long CheckFile(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumAssetBytes)
        {
            throw new InvalidDataException("An asset is empty or exceeds 16 MiB.");
        }

        return length;
    }

    private static bool ReadableImage(string root, string relative)
    {
        try
        {
            var path = AssetPath(root, relative);
            CheckFile(path);
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            return file.Read(header) >= 12 && (header[0] == 0x89 && header[1..8].SequenceEqual("PNG\r\n\x1a\n"u8)
                || header[0] == 0xff && header[1] == 0xd8
                || header[..3].SequenceEqual("GIF"u8) || header[..2].SequenceEqual("BM"u8)
                || header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RequireNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Screensaver sources and assets cannot traverse symbolic links or reparse points.");
            }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ScreensaverManifest))]
[JsonSerializable(typeof(ScreensaverPlayback))]
internal partial class ScreensaverJson : JsonSerializerContext;

internal static class ScreensaverWire
{
    internal const int MaximumPacketBytes = 512 * 1024;

    internal static async Task WriteAsync(Stream stream, ScreensaverPlayback packet, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, ScreensaverJson.Default.ScreensaverPlayback);
        if (bytes.Length > MaximumPacketBytes)
        {
            throw new InvalidDataException("Screensaver packet exceeds the protocol limit.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<ScreensaverPlayback> ReadAsync(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumPacketBytes)
        {
            throw new InvalidDataException("Invalid screensaver packet length.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, ScreensaverJson.Default.ScreensaverPlayback)
               ?? throw new InvalidDataException("Invalid screensaver packet.");
    }
}
