using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WSGM.Plugin.IntelGpu.Graphics;
using WSGM.Plugin.IntelGpu.Igcl;

namespace WSGM.Plugin.IntelGpu.Tests.Fakes;

/// <summary>Managed exports behind the real IGCL binding, enumeration, source and control paths.</summary>
internal sealed unsafe class FakeIgclDriver
{
    private readonly Dictionary<string, Delegate> _exports;
    private int _closes;
    private int _opens;
    private int _reads;
    private CtlRetroScalingSettings _settings;
    private int _writes;

    internal FakeIgclDriver()
    {
        _exports = new Dictionary<string, Delegate>
        {
            ["ctlInit"] = new InitCall(Init),
            ["ctlClose"] = new CloseCall(Close),
            ["ctlEnumerateDevices"] = new EnumerateCall(Enumerate),
            ["ctlGetDeviceProperties"] = new PropertiesCall(Properties),
            ["ctlGetSupportedRetroScalingCapability"] = new RetroCapsCall(RetroCaps),
            ["ctlGetSetRetroScaling"] = new RetroSettingsCall(RetroSettings),
            ["ctlGetSupported3DCapabilities"] = new FeatureCapsCall(FeatureCaps),
            ["ctlGetSet3DFeature"] = new FeatureCall(Feature),
            ["ctlGetIntelArcSyncInfoForMonitor"] = new ArcMonitorCall(ArcMonitor),
            ["ctlGetIntelArcSyncProfile"] = new ArcProfileCall(ArcProfile),
            ["ctlSetIntelArcSyncProfile"] = new ArcProfileCall(SetArcProfile),
            ["ctlPixelTransformationGetConfig"] = new ColorGetCall(ColorGet),
            ["ctlPixelTransformationSetConfig"] = new ColorSetCall(ColorSet),
            ["ctlGetPowerOptimizationSetting"] = new PowerCall(PowerGet),
            ["ctlSetPowerOptimizationSetting"] = new PowerCall(PowerSet)
        };
    }

    internal MemoryRegistryNode Registry { get; } = new();
    internal int Opens => Volatile.Read(ref _opens);
    internal int Closes => Volatile.Read(ref _closes);
    internal int Writes => Volatile.Read(ref _writes);
    internal ConcurrentQueue<int> ReadThreads { get; } = new();
    internal ConcurrentQueue<int> WriteThreads { get; } = new();
    internal Func<int, int>? ReadResult { get; set; }
    internal int WriteResult { get; set; }
    internal Action? BeforeRead { get; set; }
    internal Action? BeforeWrite { get; set; }
    internal Action? BeforeClose { get; set; }
    internal bool IgnoreWrites { get; set; }
    internal Ctl3dFeatureDetails[] Features { get; set; } = [];
    internal float MinimumHz { get; set; } = 30;
    internal float MaximumHz { get; set; } = 120;
    internal uint ColorBlockCount { get; set; } = 1;
    internal int ColorWrites { get; private set; }
    internal ConcurrentQueue<uint> PowerWrites { get; } = new();
    internal Action? AfterPowerWrite { get; set; }

    internal IgclSession OpenSession()
    {
        return IgclSession.TryOpen(IgclApi.Bind(Resolve, IntelLog.None)!, IntelLog.None)!;
    }

    internal IntelGpuPlugin CreatePlugin()
    {
        return new IntelGpuPlugin(log => IgclApi.Bind(Resolve, log), Registry,
            log => new IntelGraphicsMemoryTransport(Registry, AdapterClassKey.ClassPath, 0, log));
    }

    private nint Resolve(string name)
    {
        return _exports.TryGetValue(name, out var export) ? Marshal.GetFunctionPointerForDelegate(export) : 0;
    }

    private int Init(CtlInitArgs* args, nint* handle)
    {
        Interlocked.Increment(ref _opens);
        args->SupportedVersion = (1 << 16) | 1;
        *handle = 1;
        return IgclResult.Success;
    }

    private int Close(nint handle)
    {
        BeforeClose?.Invoke();
        Interlocked.Increment(ref _closes);
        return IgclResult.Success;
    }

    private static int Enumerate(nint handle, uint* count, nint* devices)
    {
        *count = 1;
        if (devices != null)
        {
            devices[0] = 2;
        }

        return IgclResult.Success;
    }

    private static int Properties(nint handle, CtlDeviceAdapterProperties* properties)
    {
        properties->PciVendorId = 0x8086;
        properties->PciDeviceId = 0x4688;
        properties->Device = 2;
        properties->GraphicsAdapterProperties = 1;
        return IgclResult.Success;
    }

    private static int RetroCaps(nint handle, CtlRetroScalingCaps* caps)
    {
        caps->SupportedRetroScaling = 3;
        return IgclResult.Success;
    }

    private int RetroSettings(nint handle, CtlRetroScalingSettings* settings)
    {
        if (settings->Get != 0)
        {
            ReadThreads.Enqueue(Environment.CurrentManagedThreadId);
            BeforeRead?.Invoke();
            var result = ReadResult?.Invoke(Interlocked.Increment(ref _reads)) ?? IgclResult.Success;
            if (result == IgclResult.Success)
            {
                settings->Enable = _settings.Enable;
                settings->RetroScalingType = _settings.RetroScalingType;
            }

            return result;
        }

        BeforeWrite?.Invoke();
        WriteThreads.Enqueue(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _writes);
        if (WriteResult == IgclResult.Success && !IgnoreWrites)
        {
            _settings = *settings;
        }

        return WriteResult;
    }

    private int FeatureCaps(nint handle, Ctl3dFeatureCaps* caps)
    {
        if (caps->FeatureDetails != 0)
        {
            var details = (Ctl3dFeatureDetails*)caps->FeatureDetails;
            for (var index = 0; index < Features.Length; index++)
            {
                details[index] = Features[index];
            }
        }

        caps->NumSupportedFeatures = (uint)Features.Length;
        return IgclResult.Success;
    }

    private static int Feature(nint handle, Ctl3dFeatureGetSet* feature)
    {
        feature->Value.EnumValue = 0;
        return IgclResult.Success;
    }

    private int ArcMonitor(nint handle, CtlArcSyncMonitorParams* monitor)
    {
        monitor->IsSupported = 1;
        monitor->MinimumHz = MinimumHz;
        monitor->MaximumHz = MaximumHz;
        return IgclResult.Success;
    }

    private static int ArcProfile(nint handle, CtlArcSyncProfileParams* profile)
    {
        profile->Profile = 1;
        profile->MinimumHz = 30;
        profile->MaximumHz = 120;
        return IgclResult.Success;
    }

    private static int SetArcProfile(nint handle, CtlArcSyncProfileParams* profile)
    {
        return IgclResult.Success;
    }

    private int ColorGet(nint handle, CtlPixTxPipeGetConfig* pipe)
    {
        if (pipe->QueryType == 1)
        {
            return IgclResult.DataNotFound;
        }

        pipe->NumBlocks = ColorBlockCount;
        if (pipe->BlockConfigs != 0)
        {
            var blocks = (CtlPixTxBlockConfig*)pipe->BlockConfigs;
            var lut = &blocks[ColorBlockCount - 1];
            lut->BlockType = 1;
            lut->Config.OneDLut.SamplingType = 0;
            lut->Config.OneDLut.SamplesPerChannel = 2;
            lut->Config.OneDLut.Channels = 1;
        }

        return IgclResult.Success;
    }

    private int ColorSet(nint handle, CtlPixTxPipeSetConfig* pipe)
    {
        ColorWrites++;
        return IgclResult.Success;
    }

    private static int PowerGet(nint handle, CtlPowerOptimizationSettings* settings)
    {
        settings->Enable = 1;
        if (settings->Feature == 8)
        {
            settings->Data.Lrr.SupportedTypes = 1;
            settings->Data.Lrr.RequirePsrDisable = 1;
        }

        return IgclResult.Success;
    }

    private int PowerSet(nint handle, CtlPowerOptimizationSettings* settings)
    {
        PowerWrites.Enqueue(settings->Feature);
        AfterPowerWrite?.Invoke();
        return IgclResult.Success;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitCall(CtlInitArgs* args, nint* handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseCall(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumerateCall(nint handle, uint* count, nint* devices);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PropertiesCall(nint handle, CtlDeviceAdapterProperties* properties);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RetroCapsCall(nint handle, CtlRetroScalingCaps* caps);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RetroSettingsCall(nint handle, CtlRetroScalingSettings* settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FeatureCapsCall(nint handle, Ctl3dFeatureCaps* caps);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FeatureCall(nint handle, Ctl3dFeatureGetSet* feature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArcMonitorCall(nint handle, CtlArcSyncMonitorParams* monitor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArcProfileCall(nint handle, CtlArcSyncProfileParams* profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ColorGetCall(nint handle, CtlPixTxPipeGetConfig* pipe);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ColorSetCall(nint handle, CtlPixTxPipeSetConfig* pipe);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerCall(nint handle, CtlPowerOptimizationSettings* settings);
}
