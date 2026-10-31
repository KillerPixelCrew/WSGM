using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One Steam collection WSGM made and keeps for a group of imported titles.</summary>
/// <remarks>
///     WSGM owns the collection by <see cref="Id" />, never by its name, and changes only the apps it
///     put in: <see cref="AppIds" /> is what the last sync added, so a title the user put in the
///     collection stays, and a title WSGM no longer imports is taken back out.
/// </remarks>
public sealed class ImportedCollection
{
    /// <summary>The owning userdata account, null until explicitly observed for legacy collections.</summary>
    public uint? AccountId { get; set; }

    /// <summary>The group it holds: a source's id.</summary>
    public string Group { get; set; } = "";

    /// <summary>Steam's id for the collection.</summary>
    public string Id { get; set; } = "";

    /// <summary>The name it was created with.</summary>
    public string Name { get; set; } = "";

    /// <summary>The imported titles the last sync put in it.</summary>
    public List<uint> AppIds { get; set; } = [];

    /// <summary>A detached copy, so a caller's changes never reach the stored record.</summary>
    internal ImportedCollection Copy()
    {
        return new ImportedCollection
        {
            AccountId = AccountId, Group = Group, Id = Id, Name = Name, AppIds = [.. AppIds]
        };
    }
}
