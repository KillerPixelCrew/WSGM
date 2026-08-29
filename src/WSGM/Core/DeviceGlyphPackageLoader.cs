using System;
using System.IO;
using WSGM.Device.Sdk.Glyphs;

namespace WSGM.Core;

/// <summary>Loads validated glyph data from the sole installed package.</summary>
internal static class DeviceGlyphPackageLoader
{
    internal static GlyphPackageImportResult Load(InstalledDevicePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Valid || package.Manifest is null)
        {
            throw new InvalidDataException("Only a valid installed package can supply glyph profiles.");
        }

        ImmutableGlyphPackageDirectorySource source = new(package.PackagePath);
        return GlyphPackageImporter.Import(source);
    }
}
