namespace WSGM.Core;

/// <summary>Concrete reason a process cannot serve as the normal shell or its creation owner.</summary>
internal enum ExplorerShellRejection
{
    /// <summary>No rejection.</summary>
    None,

    /// <summary>The process could not be opened.</summary>
    ProcessUnavailable,

    /// <summary>The process image is not the required fixed image.</summary>
    WrongImage,

    /// <summary>Windows did not expose the process image.</summary>
    ImageUnknown,

    /// <summary>The process belongs to another interactive session.</summary>
    WrongSession,

    /// <summary>Windows did not expose the process session.</summary>
    SessionUnknown,

    /// <summary>Windows did not expose token integrity.</summary>
    IntegrityUnknown,

    /// <summary>The process is not medium integrity.</summary>
    WrongIntegrity,

    /// <summary>Windows did not answer the job-membership query.</summary>
    JobMembershipUnknown,

    /// <summary>The process belongs to a job.</summary>
    JobBound,

    /// <summary>The process does not own the initialized taskbar.</summary>
    NotReady,

    /// <summary>
    ///     The canonical Explorer owns both shell surfaces but did not answer the liveness probe.
    ///     The desktop exists; only its responsiveness is unverified.
    /// </summary>
    ShellUnresponsive
}
