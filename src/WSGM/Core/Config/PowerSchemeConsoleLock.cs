namespace WSGM.Core;

/// <summary>
///     One power scheme's CONSOLELOCK values as they were before WSGM wrote
///     them. -1 = value absent (Windows default applies).
/// </summary>
public sealed class PowerSchemeConsoleLock
{
    /// <summary>Power-scheme GUID without surrounding braces.</summary>
    public string SchemeGuid { get; set; } = "";

    /// <summary>Saved AC value; <c>-1</c> means the setting was absent.</summary>
    public int AcValue { get; set; } = -1;

    /// <summary>Saved DC value; <c>-1</c> means the setting was absent.</summary>
    public int DcValue { get; set; } = -1;
}
