using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Windows power-scheme operations supplied to policy owners; native failures propagate to the caller.</summary>
internal interface IPowerSchemeApi
{
    /// <inheritdoc cref="WindowsPower.EnumerateScheme" />
    Guid? Enumerate(uint index);

    /// <inheritdoc cref="WindowsPower.ReadSchemeName" />
    string ReadName(Guid id);

    /// <inheritdoc cref="WindowsPower.GetActiveScheme" />
    Guid ReadActive();

    /// <inheritdoc cref="WindowsPower.SetActiveScheme" />
    void SetActive(Guid id);

    /// <inheritdoc cref="WindowsPower.ReadSetting" />
    uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery);

    /// <inheritdoc cref="WindowsPower.WriteSetting" />
    void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value);
}

/// <summary>Adapts the reusable library to WSGM's policy test seam.</summary>
internal sealed class WindowsPowerSchemeApi : IPowerSchemeApi
{
    /// <inheritdoc />
    public Guid? Enumerate(uint index)
    {
        return WindowsPower.EnumerateScheme(index);
    }

    /// <inheritdoc />
    public string ReadName(Guid id)
    {
        return WindowsPower.ReadSchemeName(id);
    }

    /// <inheritdoc />
    public Guid ReadActive()
    {
        return WindowsPower.GetActiveScheme();
    }

    /// <inheritdoc />
    public void SetActive(Guid id)
    {
        WindowsPower.SetActiveScheme(id);
    }

    /// <inheritdoc />
    public uint ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery)
    {
        return WindowsPower.ReadSetting(scheme, subgroup, setting, onBattery);
    }

    /// <inheritdoc />
    public void WriteSetting(Guid scheme, Guid subgroup, Guid setting, bool onBattery, uint value)
    {
        WindowsPower.WriteSetting(scheme, subgroup, setting, onBattery, value);
    }
}
