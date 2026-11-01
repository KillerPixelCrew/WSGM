using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>A folder whose shortcuts the Game Library offers as titles.</summary>
public sealed class ShortcutFolderConfig
{
    /// <summary>The file types a shortcuts folder can offer, each with its dot: all a shortcut can be.</summary>
    public static IReadOnlyList<string> AllowedExtensions { get; } = [".lnk", ".url", ".exe"];

    /// <summary>Stable identity of the folder source, <c>folder:</c> and a short token. Never reused.</summary>
    public string Id { get; set; } = "";

    /// <summary>The configured folder bound to its expected volume.</summary>
    public ManagedContentPath Root { get; set; } = new() { Directory = true };

    /// <summary>Whether folders inside it are read too.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>
    ///     The file types offered, each with its dot, such as <c>.lnk</c>; a subset of
    ///     <see cref="AllowedExtensions" />.
    /// </summary>
    public List<string> Extensions { get; set; } = [.. AllowedExtensions];

    /// <summary>Copies the folder configuration and its mutable extension list.</summary>
    /// <returns>A detached draft suitable for editing without mutating the current configuration.</returns>
    internal ShortcutFolderConfig Copy()
    {
        var copy = (ShortcutFolderConfig)MemberwiseClone();
        copy.Root = Root.Copy();
        copy.Extensions = [.. Extensions];
        return copy;
    }
}
