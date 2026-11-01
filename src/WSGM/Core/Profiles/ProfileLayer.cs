namespace WSGM.Core;

/// <summary>Which layer an edit writes to.</summary>
public enum ProfileLayer
{
    /// <summary>The running game's profile when it is enabled, Global otherwise.</summary>
    Active,

    /// <summary>Always Global.</summary>
    Global
}
