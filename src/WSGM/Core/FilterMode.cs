namespace WSGM.Core;

/// <summary>Combination logic for a group / multi-value filter.</summary>
public enum FilterMode
{
    /// <summary>Every child / value must match.</summary>
    And,

    /// <summary>At least one child / value must match.</summary>
    Or
}
