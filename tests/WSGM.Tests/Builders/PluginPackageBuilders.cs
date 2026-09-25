using System.IO.Compression;
using System.Text;
using WSGM.Core;
using WSGM.Plugin.Sdk;

namespace WSGM.Tests.Builders;

/// <summary>Writes <c>.wsgmpkg</c> files the way the packers do: a ZIP with the manifest at its root.</summary>
internal static class PluginPackageBuilders
{
    /// <summary>The <c>wsgmVersion</c> every package must carry to be admitted by this host.</summary>
    internal static string Host => PluginPackageFile.HostVersion.ToString(3);

    /// <summary>Writes a package holding the manifest and the given entries.</summary>
    /// <param name="path">The package file to create.</param>
    /// <param name="manifest">The <c>plugin.wsgm.json</c> text.</param>
    /// <param name="files">Further entries, by their name inside the package.</param>
    /// <returns>The package path.</returns>
    internal static string Write(string path, string manifest, params (string Name, byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "plugin.wsgm.json", Encoding.UTF8.GetBytes(manifest));
        foreach (var (name, bytes) in files)
        {
            WriteEntry(archive, name, bytes);
        }

        return path;
    }

    /// <summary>
    ///     Writes a common plugin package. Its entry is the small Plugin SDK image under the fixture's name: packages
    ///     refuse anything named like an assembly that is not a managed image, and the tests' loaders never run it.
    /// </summary>
    internal static string WriteCommonFixture(string directory, string id, string name = "Fixture",
        string category = "example.status")
    {
        return Write(Path.Combine(directory, id + ".wsgmpkg"), $$"""
                                                                 {"id":"{{id}}","name":"{{name}}","version":"1.0.0","category":"{{category}}",
                                                                  "entryAssembly":"Fixture.dll","entryType":"Fixture.Plugin","wsgmVersion":"{{Host}}"}
                                                                 """,
            ("Fixture.dll", File.ReadAllBytes(typeof(IPlugin).Assembly.Location)));
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        using var entry = archive.CreateEntry(name).Open();
        entry.Write(bytes);
    }
}
