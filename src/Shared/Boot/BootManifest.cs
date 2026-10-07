// Shared between WSGM and WSGM.LogonService (linked as a source file into the
// service project): the manifest is the ONLY contract between the per-user app
// and the SYSTEM service, so it must stay free of dependencies on either side —
// no Log, no ConfigStore, explicit usings (WSGM has no ImplicitUsings).

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WSGM.Core;

/// <summary>
///     Per-user sign-in policy projected into %LOCALAPPDATA%\WSGM\boot.json.
///     The service launches the named executable as the session user, optionally with that user's
///     linked elevated token. Parsing does not establish executable identity or path ownership.
/// </summary>
public sealed class BootManifest
{
    /// <summary>Manifest format version; readers skip versions they don't know.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Whether sign-in should boot into game mode at all.</summary>
    public bool GameModeBoot { get; set; }

    /// <summary>Whether Desktop-first sign-in should start the resident runtime without takeover.</summary>
    public bool DesktopResident { get; set; }

    /// <summary>
    ///     Requests the user's linked elevated token when available. WSGM projects its shared
    ///     elevation policy here so the service needs no application configuration dependency.
    /// </summary>
    public bool Elevate { get; set; }

    /// <summary>Executable path supplied by WSGM; readers must separately check availability before launch.</summary>
    public string ExePath { get; set; } = "";
}

/// <summary>Shared source-generated JSON metadata that keeps the app and service on one boot-manifest contract.</summary>
[JsonSerializable(typeof(BootManifest))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class BootManifestJsonContext : JsonSerializerContext;

/// <summary>
///     Load/save helpers for boot.json. Reading is defensive on purpose: the
///     service consumes this from SYSTEM, so garbage or truncation
///     must degrade to "disabled", never throw.
/// </summary>
public static class BootManifestStore
{
    /// <summary>File name of the manifest inside the per-user WSGM directory.</summary>
    public const string FileName = "boot.json";

    /// <summary>
    ///     Parses manifest JSON, returning null for anything unusable
    ///     (malformed JSON, wrong shape, unknown schema version, missing exe path).
    /// </summary>
    /// <param name="json">Untrusted manifest text.</param>
    /// <returns>A schema-1 manifest with a nonblank executable path, or null; no path identity check is performed.</returns>
    public static BootManifest? TryParse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize(json, BootManifestJsonContext.Default.BootManifest);
            if (manifest is null ||
                manifest.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(manifest.ExePath))
            {
                return null;
            }

            return manifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads and parses the manifest at <paramref name="path" />; null when
    ///     absent, unreadable or unparsable.
    /// </summary>
    /// <param name="path">Manifest file to read with concurrent replacement permitted.</param>
    /// <returns>The parsed manifest, or null when it cannot be read or accepted.</returns>
    public static BootManifest? TryLoad(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return TryParse(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Atomically writes the manifest to <paramref name="path" />
    ///     (temp file + replace, same pattern as configuration saves).
    /// </summary>
    /// <param name="path">Destination file, including its parent directory.</param>
    /// <param name="manifest">Policy to serialize; this method does not validate it.</param>
    /// <remarks>Creates the parent directory. Serialization and filesystem failures propagate to the caller.</remarks>
    public static void Save(string path, BootManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(manifest, BootManifestJsonContext.Default.BootManifest);
        AtomicFile.WriteText(path, json, true);
    }
}
