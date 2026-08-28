using System;
using System.IO;
using WSGM.Device.Contracts.Glyphs;

namespace WSGM.Core;

/// <summary>Loads only validated glyph data from one selected immutable package version.</summary>
internal static class DeviceGlyphPackageLoader
{
    internal static GlyphPackageImportResult Load(DevicePackageCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.Eligible || candidate.Manifest is null)
        {
            throw new InvalidDataException("Only an eligible parsed package can supply glyph profiles.");
        }

        ImmutableGlyphPackageDirectorySource source = new(candidate.PackagePath);
        return GlyphPackageImporter.Import(candidate.Manifest, source);
    }
}
