using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace WSGM.Core;

/// <summary>The Notes files on disk: one small file per page, one file per image, and the settings.</summary>
/// <remarks>
///     <para>
///         Layout under the root: <c>pages/&lt;id&gt;.json</c>, <c>assets/&lt;sha256&gt;.&lt;ext&gt;</c> and
///         <c>notes.json</c>. Editing one line of text rewrites one page file of a few kilobytes, never the
///         screenshots it shows.
///     </para>
///     <para>
///         Every file is replaced atomically and durably, the way the configuration is. A page file that does
///         not parse is moved aside rather than overwritten, and one written by a newer format is never
///         rewritten. Assets no page refers to are removed only at load, before any editor exists, so a draft
///         or an undo can never refer to an image that cleanup already deleted.
///     </para>
///     <para>Not thread-safe: <c>NotesService</c> serializes every call.</para>
/// </remarks>
public sealed class NoteStore
{
    private readonly string _root;

    /// <summary>Creates the store over a directory; nothing is read or created until used.</summary>
    /// <param name="root">The Notes directory, normally <c>%LOCALAPPDATA%\WSGM\notes</c>.</param>
    public NoteStore(string root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    private string PagesDirectory => Path.Combine(_root, "pages");
    private string AssetsDirectory => Path.Combine(_root, "assets");
    private string SettingsPath => Path.Combine(_root, "notes.json");

    /// <summary>Reads every page and the settings, sets malformed files aside and removes orphaned assets.</summary>
    /// <returns>What was found.</returns>
    /// <exception cref="NoteStoreException">The directory could not be listed.</exception>
    public NoteStoreSnapshot Load()
    {
        List<NotePage> pages = [];
        List<string> setAside = [];
        HashSet<string> readOnly = new(StringComparer.Ordinal);
        string[] files;
        try
        {
            files = Directory.Exists(PagesDirectory) ? Directory.GetFiles(PagesDirectory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NoteStoreException("WSGM's notes could not be read right now.", ex);
        }

        foreach (var file in files)
        {
            switch (ReadPage(file, out var page))
            {
                case PageRead.Read:
                    pages.Add(page!);
                    break;
                case PageRead.Newer:
                    pages.Add(page!);
                    readOnly.Add(page!.Id);
                    break;
                case PageRead.Malformed:
                    if (SetAside(file) is { } aside)
                    {
                        setAside.Add(aside);
                    }

                    break;
                case PageRead.Unavailable:
                    // Held by something else right now: it stays on disk untouched and is read next session.
                    break;
            }
        }

        var settings = ReadSettings(setAside);
        RemoveOrphanedAssets(pages);
        return new NoteStoreSnapshot(pages, settings, setAside, readOnly);
    }

    /// <summary>Replaces one page file.</summary>
    /// <param name="page">A validated page.</param>
    /// <exception cref="NoteStoreException">The page could not be written.</exception>
    public void WritePage(NotePage page)
    {
        Write(Path.Combine(PagesDirectory, page.Id + ".json"),
            JsonSerializer.Serialize(page, NoteJsonContext.Default.NotePage), "The note could not be saved.");
    }

    /// <summary>Deletes one page file. Its images stay until the next load finds them unreferenced.</summary>
    /// <param name="id">The page id.</param>
    /// <exception cref="NoteStoreException">The file exists and could not be deleted.</exception>
    public void DeletePage(string id)
    {
        try
        {
            File.Delete(Path.Combine(PagesDirectory, id + ".json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NoteStoreException("The note could not be deleted.", ex);
        }
    }

    /// <summary>Replaces the settings file.</summary>
    /// <param name="settings">The settings.</param>
    /// <exception cref="NoteStoreException">The file could not be written.</exception>
    public void WriteSettings(NoteSettings settings)
    {
        Write(SettingsPath, JsonSerializer.Serialize(settings, NoteJsonContext.Default.NoteSettings),
            "The notes settings could not be saved.");
    }

    /// <summary>Stores an encoded image once and returns its id.</summary>
    /// <param name="bytes">A PNG or JPEG image.</param>
    /// <returns>The content-addressed asset id.</returns>
    /// <exception cref="NoteStoreException">The bytes are not a PNG or JPEG, or could not be written.</exception>
    public string AddAsset(byte[] bytes)
    {
        var format = ImageHeader.Format(bytes);
        if (format is not ("png" or "jpg"))
        {
            throw new NoteStoreException("Only PNG and JPEG images can be added to a note.");
        }

        var asset = Convert.ToHexStringLower(SHA256.HashData(bytes)) + "." + format;
        var path = AssetPath(asset);
        if (File.Exists(path))
        {
            return asset;
        }

        try
        {
            Directory.CreateDirectory(AssetsDirectory);
            AtomicFile.Write(path, stream =>
            {
                stream.Write(bytes);
                return true;
            }, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NoteStoreException("The screenshot could not be saved.", ex);
        }

        return asset;
    }

    /// <summary>The file holding an asset.</summary>
    /// <param name="asset">A well-formed asset id.</param>
    /// <returns>Its path, whether or not the file exists.</returns>
    /// <exception cref="ArgumentException">The id is malformed, so it cannot name a path.</exception>
    public string AssetPath(string asset)
    {
        if (!NoteAssets.IsAssetId(asset))
        {
            throw new ArgumentException("The asset id is malformed.", nameof(asset));
        }

        return Path.Combine(AssetsDirectory, asset);
    }

    /// <summary>Reads an asset's bytes.</summary>
    /// <param name="asset">A well-formed asset id.</param>
    /// <returns>The bytes, or null when the file is missing or unreadable.</returns>
    public byte[]? ReadAsset(string asset)
    {
        try
        {
            return NoteAssets.IsAssetId(asset) ? File.ReadAllBytes(AssetPath(asset)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Notes: image {asset} could not be read: {ex.Message}");
            return null;
        }
    }

    private enum PageRead
    {
        Read,
        Newer,
        Malformed,
        Unavailable
    }

    private static PageRead ReadPage(string file, out NotePage? page)
    {
        page = null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Notes: {Path.GetFileName(file)} could not be read right now: {ex.Message}");
            return PageRead.Unavailable;
        }

        try
        {
            page = JsonSerializer.Deserialize(bytes, NoteJsonContext.Default.NotePage);
        }
        catch (JsonException ex)
        {
            Log.Warn($"Notes: {Path.GetFileName(file)} did not parse: {ex.Message}");
            return PageRead.Malformed;
        }

        if (page is null)
        {
            return PageRead.Malformed;
        }

        if (page.Format > NotePage.CurrentFormat)
        {
            // Shown but never rewritten: this build would drop whatever the newer format added.
            Log.Warn($"Notes: {Path.GetFileName(file)} was written by a newer WSGM (format {page.Format}); "
                     + "it stays read-only.");
            return NoteDocumentRules.IsId(page.Id) && NoteScope.IsValid(page.Scope)
                ? PageRead.Newer
                : PageRead.Malformed;
        }

        if (!NoteDocumentRules.TryValidate(page, out var error)
            || !string.Equals(Path.GetFileNameWithoutExtension(file), page.Id, StringComparison.Ordinal))
        {
            Log.Warn($"Notes: {Path.GetFileName(file)} is malformed: {(error.Length > 0 ? error : "its name is not its id")}");
            page = null;
            return PageRead.Malformed;
        }

        return PageRead.Read;
    }

    private NoteSettings ReadSettings(List<string> setAside)
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new NoteSettings();
            }

            var settings = JsonSerializer.Deserialize(File.ReadAllBytes(SettingsPath),
                NoteJsonContext.Default.NoteSettings) ?? new NoteSettings();
            settings.NotebookTitles ??= new Dictionary<string, string>(StringComparer.Ordinal);
            settings.CaptureCountdownSeconds = NoteSettings.ClampCountdown(settings.CaptureCountdownSeconds);
            return settings;
        }
        catch (JsonException ex)
        {
            Log.Warn($"Notes: notes.json did not parse: {ex.Message}");
            if (SetAside(SettingsPath) is { } aside)
            {
                setAside.Add(aside);
            }

            return new NoteSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Notes: notes.json could not be read right now: {ex.Message}");
            return new NoteSettings();
        }
    }

    private void RemoveOrphanedAssets(IReadOnlyList<NotePage> pages)
    {
        if (!Directory.Exists(AssetsDirectory))
        {
            return;
        }

        HashSet<string> referenced = new(pages.SelectMany(NoteDocumentRules.AssetsOf), StringComparer.Ordinal);
        try
        {
            foreach (var file in Directory.GetFiles(AssetsDirectory))
            {
                var name = Path.GetFileName(file);
                // Only files this store named are ever removed; anything else in the folder is not ours.
                if (!NoteAssets.IsAssetId(name) || referenced.Contains(name))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    Log.Info($"Notes: removed unreferenced image {name}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Notes: unreferenced image {name} could not be removed: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Notes: image cleanup skipped: {ex.Message}");
        }
    }

    private static string? SetAside(string file)
    {
        try
        {
            var aside = AtomicFile.SetAside(file);
            Log.Warn($"Notes: kept the unreadable file as {Path.GetFileName(aside)}.");
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Notes: {Path.GetFileName(file)} could not be set aside: {ex.Message}");
            return null;
        }
    }

    private static void Write(string path, string json, string failure)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteText(path, json, true,
                (temporary, ex) => Log.Warn($"Notes: temporary file {temporary} was left behind: {ex.Message}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NoteStoreException(failure, ex);
        }
    }
}
