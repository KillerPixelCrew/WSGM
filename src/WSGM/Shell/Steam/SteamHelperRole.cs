namespace WSGM.Shell;

/// <summary>What a steamwebhelper process is.</summary>
internal enum SteamHelperRole
{
    /// <summary>Its command line could not be read.</summary>
    Unknown,

    /// <summary>A renderer, GPU or utility process.</summary>
    Child,

    /// <summary>The browser process, started without remote debugging.</summary>
    Browser,

    /// <summary>The browser process, started with remote debugging.</summary>
    DebuggingBrowser
}
