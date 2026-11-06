using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Processor performance boost mode, behind WSGM's policy test seam.</summary>
internal interface ICpuBoostApi
{
    /// <summary>Reads the processor boost-mode index for the selected power source.</summary>
    /// <param name="scheme">Windows power-scheme identity.</param>
    /// <param name="onBattery">True for the DC value, false for AC.</param>
    /// <returns>The stored Windows boost-mode index; native failures propagate.</returns>
    uint Read(Guid scheme, bool onBattery);

    /// <summary>Writes the boost-mode index without activating the scheme.</summary>
    /// <param name="scheme">Windows power-scheme identity.</param>
    /// <param name="onBattery">True for the DC value, false for AC.</param>
    /// <param name="value">Windows boost-mode index already validated by the policy owner.</param>
    void Write(Guid scheme, bool onBattery, uint value);

    /// <summary>Marks the setting visible in Windows' own power options, as HC does before it writes.</summary>
    /// <returns>Whether Windows accepted the attribute; a refusal changes nothing about the value.</returns>
    bool TryReveal();
}

/// <summary>Adapts the reusable library to WSGM's policy test seam.</summary>
internal sealed class WindowsCpuBoostApi : ICpuBoostApi
{
    /// <inheritdoc />
    public uint Read(Guid scheme, bool onBattery)
    {
        return WindowsPower.ReadSetting(scheme, WindowsPower.SubgroupProcessor, WindowsPower.SettingProcessorBoostMode,
            onBattery);
    }

    /// <inheritdoc />
    public void Write(Guid scheme, bool onBattery, uint value)
    {
        WindowsPower.WriteSetting(scheme, WindowsPower.SubgroupProcessor, WindowsPower.SettingProcessorBoostMode,
            onBattery, value);
    }

    /// <inheritdoc />
    /// <remarks>HC's <c>SetAttribute(..., 2u)</c>: <c>POWER_ATTRIBUTE_SHOW_AOAC</c>, which also clears the hidden bit.</remarks>
    public bool TryReveal()
    {
        return WindowsPower.TryWriteSettingAttributes(WindowsPower.SubgroupProcessor,
            WindowsPower.SettingProcessorBoostMode, PowerSettingAttributes.ShowAoac, out _);
    }
}
