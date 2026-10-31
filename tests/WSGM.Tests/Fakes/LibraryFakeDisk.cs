using WSGM.Core;

namespace WSGM.Tests.Fakes;

/// <summary>An in-memory file system for the Game Library's folder-based sources.</summary>
/// <remarks>Folders exist when a file is under them, as they would after a real install.</remarks>
internal sealed class LibraryFakeDisk
{
    private readonly Dictionary<string, FileAttributes> _attributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The special folders of a user called <c>u</c>.</summary>
    public static string SpecialFolder(Environment.SpecialFolder folder)
    {
        return folder switch
        {
            Environment.SpecialFolder.ApplicationData => @"C:\Users\u\AppData\Roaming",
            // wsgm-allow-live-data-path: names the folder only to answer with the fake user's path;
            // nothing here resolves the real one.
            Environment.SpecialFolder.LocalApplicationData => @"C:\Users\u\AppData\Local",
            _ => ""
        };
    }

    /// <summary>Adds a file.</summary>
    public LibraryFakeDisk With(string path, string text = "")
    {
        _files[path] = text;
        return this;
    }

    /// <summary>Gives a file or folder attributes such as hidden.</summary>
    public LibraryFakeDisk Mark(string path, FileAttributes attributes)
    {
        _attributes[path] = attributes;
        return this;
    }

    public bool FileExists(string path)
    {
        return _files.ContainsKey(path);
    }

    public string? ReadText(string path)
    {
        return _files.GetValueOrDefault(path);
    }

    public bool DirectoryExists(string path)
    {
        var prefix = Prefix(path);
        return _files.Keys.Any(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> Directories(string path)
    {
        var prefix = Prefix(path);
        return _files.Keys
            .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(file => file[prefix.Length..])
            .Where(rest => rest.Contains('\\'))
            .Select(rest => prefix + rest[..rest.IndexOf('\\')])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FolderEntry> List(string path)
    {
        var prefix = Prefix(path);
        var files = _files.Keys
            .Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                           && !file[prefix.Length..].Contains('\\'))
            .Select(file => new FolderEntry(file, false, _attributes.GetValueOrDefault(file, FileAttributes.Normal)));
        var folders = Directories(path)
            .Select(folder => new FolderEntry(
                folder, true, _attributes.GetValueOrDefault(folder, FileAttributes.Directory)));
        return files.Concat(folders).ToList();
    }

    private static string Prefix(string path)
    {
        return path.TrimEnd('\\') + "\\";
    }
}
