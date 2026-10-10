using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using WSGM.Install;

namespace WSGM.Setup.Engine;

/// <summary>Identifies former built-in packages replaced by direct native libraries.</summary>
internal static class NeutralLibraryPackageRetirement
{
    internal static bool IsRetiredPackage(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var manifests = archive.Entries.Where(entry => entry.FullName == "plugin.wsgm.json").ToArray();
            if (manifests.Length != 1 || manifests[0].Length > 1024 * 1024)
            {
                return false;
            }

            using var stream = manifests[0].Open();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("id", out var id)
                   && id.ValueKind == JsonValueKind.String
                   && NeutralLibraryRetirement.PackageIds.Contains(id.GetString(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
