namespace WSGM.Core;

/// <summary>How much detail the log records, as a user-facing choice.</summary>
/// <remarks>
///     Two states rather than a full level picker: the only useful question is whether debug detail is
///     wanted. Hiding warnings or failures is never a reasonable choice, because this log is the whole
///     of remote diagnosis.
/// </remarks>
public enum LogVerbosity
{
    /// <summary>Transitions, warnings and failures. The default.</summary>
    Normal,

    /// <summary>Adds the debug level, for reproducing a specific problem.</summary>
    Verbose
}
