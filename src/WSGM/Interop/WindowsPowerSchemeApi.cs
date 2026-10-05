using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

internal interface IPowerSchemeApi
{
    Guid? Enumerate(uint index);
    string ReadName(Guid id);
    Guid ReadActive();
    void SetActive(Guid id);
    uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery);
    void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value);
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

    public uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        return WindowsPower.ReadSetting(scheme, subgroup, setting, onBattery);
    }

    public void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
    {
        WindowsPower.WriteSetting(scheme, subgroup, setting, onBattery, value);
    }
}
