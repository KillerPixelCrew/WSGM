namespace WSGM.Core;

/// <summary>One recorded change of an in-flight transaction, enough to undo it after a crash.</summary>
internal sealed record GameToolJournalEntry
{
    /// <summary>The path relative to the target directory.</summary>
    public string RelativePath { get; init; } = "";

    /// <summary>What the transaction does to the file.</summary>
    public GameToolJournalAction Action { get; init; }

    /// <summary>The hash of the content written, empty for a deletion.</summary>
    public string NewSha256 { get; init; } = "";

    /// <summary>The backup of the previous content, empty for a creation.</summary>
    public string BackupPath { get; init; } = "";

    /// <summary>The hash of the previous content, empty for a creation.</summary>
    public string PreviousSha256 { get; init; } = "";
}
