using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
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

    private SetupPayload(ZipArchive? archive, string? directory, BundleManifest bundle)
    {
        _archive = archive;
        _directory = directory;
        Bundle = bundle;
    }

    /// <summary>What the release bundles.</summary>
    public BundleManifest Bundle { get; }

    /// <summary>Where the payload came from, for the log.</summary>
    public string Source => _directory ?? "embedded";

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
            return File.Exists(bundlePath)
                ? new SetupPayload(null, root, BundleManifest.Parse(File.ReadAllBytes(bundlePath)))
                : throw new FileNotFoundException("The payload directory has no bundle.json.", bundlePath);
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        ZipArchive archive = new(stream, ZipArchiveMode.Read, false);
        var entry = archive.GetEntry("bundle.json") ?? throw new InvalidDataException("The payload has no bundle.json.");
        using var bundleStream = entry.Open();
        using MemoryStream buffer = new();
        bundleStream.CopyTo(buffer);
        return new SetupPayload(archive, null, BundleManifest.Parse(buffer.ToArray()));
    }

    /// <summary>Copies one payload folder (for example <c>App</c>) into a directory.</summary>
    /// <param name="folder">Top-level payload folder.</param>
    /// <param name="destination">Target directory; created if missing.</param>
    public void Extract(string folder, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var (relative, open) in Files(folder))
        {
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The payload names an unsafe path: {relative}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = open();
            using var output = File.Create(target);
            source.CopyTo(output);
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
            yield return (captured.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar), () => captured.Open());
        }
    }
}
