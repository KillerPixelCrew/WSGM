using System;
using System.Runtime.InteropServices;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Processor performance boost mode, behind WSGM's policy test seam.</summary>
internal interface ICpuBoostApi
{
    Guid ReadActiveScheme();
    uint Read(Guid scheme, bool onBattery);
    void Write(Guid scheme, bool onBattery, uint value);

    /// <summary>Marks the setting visible in Windows' own power options, as HC does before it writes.</summary>
    /// <returns>Whether Windows accepted the attribute; a refusal changes nothing about the value.</returns>
    bool TryReveal();

    void RefreshActiveScheme();
}

/// <summary>Adapts the reusable library to WSGM's policy test seam.</summary>
internal sealed partial class WindowsCpuBoostApi : ICpuBoostApi
{
    /// <summary>HC's <c>SetAttribute(..., 2u)</c>: <c>POWER_ATTRIBUTE_SHOW_AOAC</c>, which also clears the hidden bit.</summary>
    private const uint ShowAttributes = 2;

    /// <summary>The processor power settings subgroup, HC <c>PowerSubGroup.SUB_PROCESSOR</c>.</summary>
    internal static readonly Guid ProcessorSubgroup = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Processor performance boost mode, HC <c>PowerSetting.PERFBOOSTMODE</c>.</summary>
    internal static readonly Guid BoostModeSetting = new("be337238-0d82-4146-a960-4f3749d470c7");

    public Guid ReadActiveScheme()
    {
        return WindowsPower.GetActiveScheme();
    }

    public uint Read(Guid scheme, bool onBattery)
    {
        return WindowsPower.ReadSetting(scheme, ProcessorSubgroup, BoostModeSetting, onBattery);
    }

    public void Write(Guid scheme, bool onBattery, uint value)
    {
        WindowsPower.WriteSetting(scheme, ProcessorSubgroup, BoostModeSetting, onBattery, value);
    }

    public bool TryReveal()
    {
        return PowerWriteSettingAttributes(in ProcessorSubgroup, in BoostModeSetting, ShowAttributes) == 0;
    }

    public void RefreshActiveScheme()
    {
        WindowsPower.RefreshActiveScheme();
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteSettingAttributes(in Guid subgroup, in Guid setting, uint attributes);
}
