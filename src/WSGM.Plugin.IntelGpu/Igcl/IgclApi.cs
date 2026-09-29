using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Plugin.IntelGpu.Igcl;

/// <summary>
///     The Graphics Control Library entry points, bound from the driver's own <c>ControlLib.dll</c>.
/// </summary>
/// <remarks>
///     <c>ControlLib.dll</c> ships with the Intel driver into <c>System32</c>, so it is loaded by name
///     and its absence simply means unsupported. Nothing is vendored. The core entry points are required;
///     every feature entry point is optional, because an older driver lacks some and must still offer
///     the rest. A missing optional entry point leaves that pointer null and the feature unpublished.
/// </remarks>
internal sealed unsafe class IgclApi : IDisposable
{
    private nint _library;

    private IgclApi(nint library)
    {
        _library = library;
    }

    public delegate* unmanaged[Cdecl]<CtlInitArgs*, nint*, int> Init { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, int> Close { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> EnumerateDevices { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, uint*, nint*, int> EnumerateDisplayOutputs { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlDeviceAdapterProperties*, int> GetDeviceProperties
    {
        get;
        private set;
    }

    public delegate* unmanaged[Cdecl]<nint, CtlDisplayProperties*, int> GetDisplayProperties { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlDisplayEncoderProperties*, int> GetEncoderProperties
    {
        get;
        private set;
    }

    public delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureCaps*, int> GetSupported3dCapabilities { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureGetSet*, int> GetSet3dFeature { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlRetroScalingCaps*, int> GetRetroScalingCaps { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlRetroScalingSettings*, int> GetSetRetroScaling { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlArcSyncMonitorParams*, int> GetArcSyncInfo { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlArcSyncProfileParams*, int> GetArcSyncProfile { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlArcSyncProfileParams*, int> SetArcSyncProfile { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlScalingCaps*, int> GetScalingCaps { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlScalingSettings*, int> GetScaling { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlScalingSettings*, int> SetScaling { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlSharpnessCaps*, int> GetSharpnessCaps { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlSharpnessSettings*, int> GetSharpness { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlSharpnessSettings*, int> SetSharpness { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlPowerOptimizationCaps*, int> GetPowerCaps { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlPowerOptimizationSettings*, int> GetPowerSetting
    {
        get;
        private set;
    }

    public delegate* unmanaged[Cdecl]<nint, CtlPowerOptimizationSettings*, int> SetPowerSetting
    {
        get;
        private set;
    }

    public delegate* unmanaged[Cdecl]<nint, CtlLaceConfig*, int> GetLace { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlLaceConfig*, int> SetLace { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlWireFormatConfig*, int> GetSetWireFormat { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlDisplaySettings*, int> GetSetDisplaySettings { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlPixTxPipeGetConfig*, int> PixTxGetConfig { get; private set; }

    public delegate* unmanaged[Cdecl]<nint, CtlPixTxPipeSetConfig*, int> PixTxSetConfig { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_library == 0)
        {
            return;
        }

        NativeLibrary.Free(_library);
        _library = 0;
    }

    /// <summary>Loads the driver's library and binds its entry points.</summary>
    /// <param name="log">Receives what was missing, once per change, since a missing driver is retried.</param>
    /// <returns>The bound library, or null when it is absent or lacks a core entry point.</returns>
    public static IgclApi? TryLoad(IntelLog log)
    {
        if (!NativeLibrary.TryLoad("ControlLib.dll", out var library))
        {
            log.Change(DeviceTraceLevel.Info, "igcl", "library",
                "ControlLib.dll is not present; the Intel driver is not installed.");
            return null;
        }

        IgclApi api = new(library);
        if (api.TryBind(log))
        {
            log.Change(DeviceTraceLevel.Info, "igcl", "library", "ControlLib.dll is loaded.");
            return api;
        }

        api.Dispose();
        return null;
    }

    private bool TryBind(IntelLog log)
    {
        // Addresses first, cast at the end: a function-pointer type cannot be a generic argument, so
        // it cannot be threaded through one shared helper.
        if (!Required("ctlInit", out var init)
            || !Required("ctlClose", out var close)
            || !Required("ctlEnumerateDevices", out var enumerateDevices))
        {
            return false;
        }

        Init = (delegate* unmanaged[Cdecl]<CtlInitArgs*, nint*, int>)init;
        Close = (delegate* unmanaged[Cdecl]<nint, int>)close;
        EnumerateDevices = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumerateDevices;
        EnumerateDisplayOutputs = (delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)Optional(
            "ctlEnumerateDisplayOutputs");
        GetDeviceProperties = (delegate* unmanaged[Cdecl]<nint, CtlDeviceAdapterProperties*, int>)Optional(
            "ctlGetDeviceProperties");
        GetDisplayProperties = (delegate* unmanaged[Cdecl]<nint, CtlDisplayProperties*, int>)Optional(
            "ctlGetDisplayProperties");
        GetEncoderProperties = (delegate* unmanaged[Cdecl]<nint, CtlDisplayEncoderProperties*, int>)Optional(
            "ctlGetAdaperDisplayEncoderProperties");
        GetSupported3dCapabilities = (delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureCaps*, int>)Optional(
            "ctlGetSupported3DCapabilities");
        GetSet3dFeature = (delegate* unmanaged[Cdecl]<nint, Ctl3dFeatureGetSet*, int>)Optional(
            "ctlGetSet3DFeature");
        GetRetroScalingCaps = (delegate* unmanaged[Cdecl]<nint, CtlRetroScalingCaps*, int>)Optional(
            "ctlGetSupportedRetroScalingCapability");
        GetSetRetroScaling = (delegate* unmanaged[Cdecl]<nint, CtlRetroScalingSettings*, int>)Optional(
            "ctlGetSetRetroScaling");
        GetArcSyncInfo = (delegate* unmanaged[Cdecl]<nint, CtlArcSyncMonitorParams*, int>)Optional(
            "ctlGetIntelArcSyncInfoForMonitor");
        GetArcSyncProfile = (delegate* unmanaged[Cdecl]<nint, CtlArcSyncProfileParams*, int>)Optional(
            "ctlGetIntelArcSyncProfile");
        SetArcSyncProfile = (delegate* unmanaged[Cdecl]<nint, CtlArcSyncProfileParams*, int>)Optional(
            "ctlSetIntelArcSyncProfile");
        GetScalingCaps = (delegate* unmanaged[Cdecl]<nint, CtlScalingCaps*, int>)Optional(
            "ctlGetSupportedScalingCapability");
        GetScaling = (delegate* unmanaged[Cdecl]<nint, CtlScalingSettings*, int>)Optional("ctlGetCurrentScaling");
        SetScaling = (delegate* unmanaged[Cdecl]<nint, CtlScalingSettings*, int>)Optional("ctlSetCurrentScaling");
        GetSharpnessCaps = (delegate* unmanaged[Cdecl]<nint, CtlSharpnessCaps*, int>)Optional("ctlGetSharpnessCaps");
        GetSharpness = (delegate* unmanaged[Cdecl]<nint, CtlSharpnessSettings*, int>)Optional(
            "ctlGetCurrentSharpness");
        SetSharpness = (delegate* unmanaged[Cdecl]<nint, CtlSharpnessSettings*, int>)Optional(
            "ctlSetCurrentSharpness");
        GetPowerCaps = (delegate* unmanaged[Cdecl]<nint, CtlPowerOptimizationCaps*, int>)Optional(
            "ctlGetPowerOptimizationCaps");
        GetPowerSetting = (delegate* unmanaged[Cdecl]<nint, CtlPowerOptimizationSettings*, int>)Optional(
            "ctlGetPowerOptimizationSetting");
        SetPowerSetting = (delegate* unmanaged[Cdecl]<nint, CtlPowerOptimizationSettings*, int>)Optional(
            "ctlSetPowerOptimizationSetting");
        GetLace = (delegate* unmanaged[Cdecl]<nint, CtlLaceConfig*, int>)Optional("ctlGetLACEConfig");
        SetLace = (delegate* unmanaged[Cdecl]<nint, CtlLaceConfig*, int>)Optional("ctlSetLACEConfig");
        GetSetWireFormat = (delegate* unmanaged[Cdecl]<nint, CtlWireFormatConfig*, int>)Optional(
            "ctlGetSetWireFormat");
        GetSetDisplaySettings = (delegate* unmanaged[Cdecl]<nint, CtlDisplaySettings*, int>)Optional(
            "ctlGetSetDisplaySettings");
        PixTxGetConfig = (delegate* unmanaged[Cdecl]<nint, CtlPixTxPipeGetConfig*, int>)Optional(
            "ctlPixelTransformationGetConfig");
        PixTxSetConfig = (delegate* unmanaged[Cdecl]<nint, CtlPixTxPipeSetConfig*, int>)Optional(
            "ctlPixelTransformationSetConfig");
        return true;

        bool Required(string name, out nint address)
        {
            if (NativeLibrary.TryGetExport(_library, name, out address))
            {
                return true;
            }

            log.Change(DeviceTraceLevel.Warn, "igcl", "library", $"ControlLib.dll lacks {name}.");
            return false;
        }

        nint Optional(string name)
        {
            return NativeLibrary.TryGetExport(_library, name, out var address) ? address : 0;
        }
    }
}
