using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

internal interface IPowerSchemeApi
{
    Guid? Enumerate(uint index);
    string ReadName(Guid id);
    Guid ReadActive();
    void SetActive(Guid id);
}

/// <summary>Adapts the reusable library to WSGM's policy test seam.</summary>
internal sealed class WindowsPowerSchemeApi : IPowerSchemeApi
{
    public Guid? Enumerate(uint index)
    {
        return WindowsPower.EnumerateScheme(index);
    }

    public string ReadName(Guid id)
    {
        return WindowsPower.ReadSchemeName(id);
    }

    public Guid ReadActive()
    {
        return WindowsPower.GetActiveScheme();
    }

    public void SetActive(Guid id)
    {
        WindowsPower.SetActiveScheme(id);
    }
}
