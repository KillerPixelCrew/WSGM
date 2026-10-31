namespace WSGM.Core;

/// <summary>What launch route a packaged title needs.</summary>
public enum XboxRuntime
{
    /// <summary>Neither shape could be established from the package's own metadata.</summary>
    Unknown,

    /// <summary>A native UWP title, which runs in an AppContainer.</summary>
    NativeUwp,

    /// <summary>A full-trust packaged Win32 title with GDK evidence.</summary>
    PackagedWin32Gdk
}
