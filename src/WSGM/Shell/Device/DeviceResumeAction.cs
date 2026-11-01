namespace WSGM.Shell;

/// <summary>What a resume does with the cycle it finds.</summary>
internal enum DeviceResumeAction
{
    /// <summary>Nothing to resume.</summary>
    Skip,

    /// <summary>Resume the suspended handheld in place.</summary>
    Resume,

    /// <summary>Stop whatever is there and start a fresh cycle.</summary>
    Restart
}
