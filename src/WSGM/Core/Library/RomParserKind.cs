namespace WSGM.Core;

/// <summary>The actual entry-point parsing required by a system's supported launch formats.</summary>
public enum RomParserKind
{
    /// <summary>An individual ROM file.</summary>
    File,

    /// <summary>A disc image or descriptor with required companions.</summary>
    Disc,

    /// <summary>A verified PlayStation 3 extracted/installed EBOOT entry point.</summary>
    Eboot,

    /// <summary>An installed Vita/PS4 application selected by its eboot.bin entry point.</summary>
    InstalledEboot
}
