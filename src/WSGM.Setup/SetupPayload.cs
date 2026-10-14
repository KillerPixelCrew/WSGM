using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using WSGM.Install;

namespace WSGM.Setup;

/// <summary>
///     What this setup carries: the application (<c>App/</c>), the virtual-controller stack
///     (<c>Controller/</c>), every bundled plugin (<c>Packages/</c>) and <c>bundle.json</c>. A release
///     embeds it as one ZIP; a development build points at a publish directory with <c>/payload=</c>.
/// </summary>
internal sealed class SetupPayload : IDisposable
{
    private const string ResourceName = "WSGM.Setup.Payload.zip";
    private readonly ZipArchive? _archive;
    private readonly string? _directory;

    private SetupPayload(ZipArchive? archive, string? directory, BundleManifest bundle, bool? online)
    {
        _archive = archive;
        _directory = directory;
        Bundle = bundle;
        Online = online == true;
        DistributionSpecified = online is not null;
    }

    /// <summary>What the release bundles.</summary>
    public BundleManifest Bundle { get; }

    /// <summary>Whether missing prerequisites are downloaded rather than carried in the payload.</summary>
    public bool Online { get; }

    /// <summary>Whether this payload explicitly selected online or offline prerequisite acquisition.</summary>
    internal bool DistributionSpecified { get; }

    /// <summary>Explains prerequisite acquisition before setup changes the machine.</summary>
    public string PrerequisitesNotice => Online
        ? "Online setup needs an internet connection to download missing WebView2, selected controller drivers and RTSS."
        : DistributionSpecified
            ? "Offline setup includes prerequisite installers. No internet connection is needed."
            : "This setup includes WebView2 and controller drivers. Selected RTSS needs an internet connection if it is missing.";

    /// <summary>Where the payload came from, for the log.</summary>
    public string Source => _directory ?? "embedded";

    /// <summary>Closes the owned embedded archive; development-directory files remain caller-owned.</summary>
    public void Dispose()
    {
        _archive?.Dispose();
    }

    /// <summary>Opens the embedded payload, or the given directory.</summary>
    /// <param name="directory">A development payload directory, or null for the embedded one.</param>
    /// <returns>The payload, or null when this build carries none.</returns>
    public static SetupPayload? Open(string? directory)
    {
        if (directory is not null)
        {
            var root = Path.GetFullPath(directory);
            var bundlePath = Path.Combine(root, "bundle.json");
            var distributionPath = Path.Combine(root, "setup-distribution.json");
            return File.Exists(bundlePath)
                ? new SetupPayload(null, root, BundleManifest.Parse(File.ReadAllBytes(bundlePath)),
                    File.Exists(distributionPath) ? ReadDistribution(File.ReadAllBytes(distributionPath)) : null)
                : throw new FileNotFoundException("The payload directory has no bundle.json.", bundlePath);
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        return OpenArchive(stream);
    }

    /// <summary>Takes ownership of a payload stream, including when opening or parsing fails.</summary>
    /// <param name="stream">Readable ZIP stream whose lifetime transfers to the payload.</param>
    /// <returns>The archive owner with its parsed bundle manifest.</returns>
    /// <exception cref="InvalidDataException">The archive or its bundle manifest cannot be accepted.</exception>
    internal static SetupPayload OpenArchive(Stream stream)
    {
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
            var entry = archive.GetEntry("bundle.json") ??
                        throw new InvalidDataException("The payload has no bundle.json.");
            using var bundleStream = entry.Open();
            using MemoryStream buffer = new();
            bundleStream.CopyTo(buffer);
            bool? online = null;
            if (archive.GetEntry("setup-distribution.json") is { } distribution)
            {
                using var distributionStream = distribution.Open();
                using MemoryStream distributionBuffer = new();
                distributionStream.CopyTo(distributionBuffer);
                online = ReadDistribution(distributionBuffer.ToArray());
            }

            return new SetupPayload(archive, null, BundleManifest.Parse(buffer.ToArray()), online);
        }
        catch
        {
            if (archive is null)
            {
                stream.Dispose();
            }
            else
            {
                archive.Dispose();
            }

            throw;
        }
    }

    /// <summary>Copies one payload folder (for example <c>App</c>) into a directory.</summary>
    /// <param name="folder">Top-level payload folder.</param>
    /// <param name="destination">Target directory; created if missing.</param>
    /// <param name="include">Optional relative-file filter, used to extract controller runtime without installers.</param>
    public void Extract(string folder, string destination, Func<string, bool>? include = null)
    {
        Directory.CreateDirectory(destination);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        foreach (var (relative, open) in Files(folder))
        {
            if (include is not null && !include(relative))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The payload names an unsafe path: {relative}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = open();
            using var output = File.Create(target);
            source.CopyTo(output);
        }
    }

    private static bool ReadDistribution(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("prerequisites", out var prerequisites))
            {
                throw new InvalidDataException("The setup distribution marker is invalid. Use a complete WSGM setup.");
            }

            return prerequisites.ValueKind == JsonValueKind.String ? prerequisites.GetString() switch
            {
                "download" => true,
                "bundled" => false,
                _ => throw new InvalidDataException("The setup prerequisite mode is unsupported. Use a complete WSGM setup.")
            } : throw new InvalidDataException("The setup prerequisite mode is invalid. Use a complete WSGM setup.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The setup distribution marker is invalid. Use a complete WSGM setup.", ex);
        }
    }

    /// <summary>Copies one payload file to a path.</summary>
    /// <param name="relative">Path inside the payload, for example <c>Packages/x.wsgmpkg</c>.</param>
    /// <param name="destination">Target file.</param>
    public void ExtractFile(string relative, string destination)
    {
        var folder = relative.Split('/')[0];
        var inner = relative[(folder.Length + 1)..];
        var (_, open) = Files(folder).FirstOrDefault(file => file.Relative.Replace('\\', '/') == inner);
        if (open is null)
        {
            throw new FileNotFoundException("The payload has no " + relative);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = open();
        using var output = File.Create(destination);
        source.CopyTo(output);
    }

    private IEnumerable<(string Relative, Func<Stream> Open)> Files(string folder)
    {
        if (_directory is not null)
        {
            var root = Path.Combine(_directory, folder);
            if (!Directory.Exists(root))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                yield return (Path.GetRelativePath(root, file), () => File.OpenRead(file));
            }

            yield break;
        }

        var prefix = folder + "/";
        foreach (var entry in _archive!.Entries.Where(entry =>
                     entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && !entry.FullName.EndsWith('/')))
        {
            var captured = entry;
            yield return (captured.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar),
                () => captured.Open());
        }
    }
}
