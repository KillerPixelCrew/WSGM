using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

internal interface IPowerModeApi
{
    Guid Read();
    void Set(Guid mode);
}

internal sealed class WindowsPowerModeApi : IPowerModeApi
{
    public Guid Read() => WindowsPower.GetEffectiveMode();
    public void Set(Guid mode) => WindowsPower.SetActiveMode(mode);
}
