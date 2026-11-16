namespace WSGM.Core;

/// <summary>What a sync found in ES-DE's ROM folder setting.</summary>
internal enum EsDeRomFolderSetting
{
    /// <summary>ES-DE already reads WSGM's ROM folder.</summary>
    Unchanged,

    /// <summary>The setting was empty or missing, or Sync now replaced it, and now names WSGM's ROM folder.</summary>
    Set,

    /// <summary>The user chose another ROM folder in ES-DE; it was left as it is.</summary>
    Elsewhere,

    /// <summary>ES-DE is running and rewrites its settings, so the change waits until it is closed.</summary>
    Waiting
}
