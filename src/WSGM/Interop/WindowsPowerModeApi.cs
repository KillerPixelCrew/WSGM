using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

internal interface IPowerModeApi
{
    Guid Read();
    void Set(Guid mode);
}

internal sealed partial class WindowsPowerModeApi : IPowerModeApi
{
    public Guid Read()
    {
        uint result = PowerGetEffectiveOverlayScheme(out Guid mode);
        if (result != 0) { throw new Win32Exception(unchecked((int)result)); }
        return mode;
    }

    public void Set(Guid mode)
    {
        uint result = PowerSetActiveOverlayScheme(mode);
        if (result != 0) { throw new Win32Exception(unchecked((int)result)); }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid mode);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveOverlayScheme(Guid mode);
}
