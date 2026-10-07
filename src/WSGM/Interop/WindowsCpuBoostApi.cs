using System;
using System.Runtime.InteropServices;
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
internal sealed partial class WindowsCpuBoostApi : ICpuBoostApi
{
    /// <summary>HC's <c>SetAttribute(..., 2u)</c>: <c>POWER_ATTRIBUTE_SHOW_AOAC</c>, which also clears the hidden bit.</summary>
    private const uint ShowAttributes = 2;

    /// <summary>Processor performance boost mode, HC <c>PowerSetting.PERFBOOSTMODE</c>.</summary>
    internal static readonly Guid BoostModeSetting = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <inheritdoc />
    public uint Read(Guid scheme, bool onBattery)
    {
        return WindowsPower.ReadSetting(scheme, WindowsPower.SubgroupProcessor, BoostModeSetting, onBattery);
    }

    /// <inheritdoc />
    public void Write(Guid scheme, bool onBattery, uint value)
    {
        WindowsPower.WriteSetting(scheme, WindowsPower.SubgroupProcessor, BoostModeSetting, onBattery, value);
    }

    /// <inheritdoc />
    public bool TryReveal()
    {
        return PowerWriteSettingAttributes(
            in WindowsPower.SubgroupProcessor, in BoostModeSetting, ShowAttributes) == 0;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteSettingAttributes(in Guid subgroup, in Guid setting, uint attributes);
}
