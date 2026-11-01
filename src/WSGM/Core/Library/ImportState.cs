using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Everything WSGM remembers about the entries it created, and what the user decided.</summary>
internal sealed class ImportState
{
    /// <summary>The file format this build writes.</summary>
    internal const int CurrentVersion = ManagedContentStorage.FormatVersion;

    /// <summary>The format the file was written in. Zero for a file from before versions were recorded.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>One record per generated entry, oldest first.</summary>
    public List<ImportedEntry> Entries { get; set; } = [];

    /// <summary>One choice per title the user has decided something about, oldest first.</summary>
    public List<ImportChoice> Choices { get; set; } = [];

    /// <summary>The Steam collections WSGM made for its imports, one per group.</summary>
    public List<ImportedCollection> Collections { get; set; } = [];

    /// <summary>Accepted local storage identities and their manifest revisions.</summary>
    public List<StorageLibraryRecord> Libraries { get; set; } = [];

    /// <summary>The card each Steam shortcut's literal drive path was last found on, per account.</summary>
    public List<ShortcutBinding> ShortcutBindings { get; set; } = [];
}
