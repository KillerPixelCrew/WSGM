namespace WSGM.Shell;

/// <summary>One line of an installation's setup checklist: a declared prerequisite or its required runtimes.</summary>
/// <param name="Kind">The prerequisite kind the configure command takes, or <c>runtime</c>.</param>
/// <param name="Name">What is needed, such as "PlayStation 2 BIOS".</param>
/// <param name="Ready">Whether it was found when the installation was last checked.</param>
/// <param name="Detail">Where it was found, or what to supply.</param>
/// <param name="Required">Whether games refuse to start without it.</param>
internal sealed record EmulatorSetupItem(string Kind, string Name, bool Ready, string Detail, bool Required)
{
    /// <summary>File types the chooser accepts; empty accepts any file.</summary>
    public string[] Extensions { get; init; } = [];

    /// <summary>Whether a folder may be chosen instead of a file.</summary>
    public bool AllowDirectory { get; init; }
}
