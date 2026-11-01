namespace WSGM.Core;

/// <summary>The kind of an owned file.</summary>
internal enum GameToolFileKind
{
    /// <summary>A binary removed on uninstall only while its hash still matches.</summary>
    Binary,

    /// <summary>A configuration file the tool edits itself; preserved on update and always removed on uninstall.</summary>
    Config
}
