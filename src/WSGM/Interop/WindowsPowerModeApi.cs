using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Reads effective Windows power mode and requests an overlay mode without changing WSGM policy.</summary>
internal interface IPowerModeApi
{
    /// <inheritdoc cref="WindowsPower.GetEffectiveMode" />
    Guid Read();

    /// <inheritdoc cref="WindowsPower.SetActiveMode" />
    void Set(Guid mode);
}

/// <summary>Adapts WindowsDeviceControl power-mode operations to the session policy seam.</summary>
internal sealed class WindowsPowerModeApi : IPowerModeApi
{
    /// <inheritdoc />
    public Guid Read()
    {
        return WindowsPower.GetEffectiveMode();
    }

    /// <inheritdoc />
    public void Set(Guid mode)
    {
        WindowsPower.SetActiveMode(mode);
    }
}
