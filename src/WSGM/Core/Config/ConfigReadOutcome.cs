namespace WSGM.Core;

/// <summary>Whether configuration was read, absent, corrupt or temporarily inaccessible.</summary>
public enum ConfigReadOutcome
{
    /// <summary>An existing document was loaded.</summary>
    Loaded,

    /// <summary>No document exists; defaults are available.</summary>
    Absent,

    /// <summary>The existing document could not be parsed.</summary>
    Corrupt,

    /// <summary>The file or its mutex could not be read.</summary>
    Unreadable
}
