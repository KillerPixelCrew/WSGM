using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>Durable promotion record used to complete a commit or restore replaced theme entries.</summary>
internal sealed class ThemeUpdateJournal
{
    /// <summary>Unique transaction suffix identifying the sibling staging and backup directories.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Top-level entries being promoted from staging.</summary>
    public List<string> Names { get; set; } = [];

    /// <summary>Entries that existed before promotion and therefore require backup restoration.</summary>
    public List<string> Existing { get; set; } = [];

    /// <summary>Whether promotion completed and recovery should retire backups instead of rolling back.</summary>
    public bool Committed { get; set; }
}
