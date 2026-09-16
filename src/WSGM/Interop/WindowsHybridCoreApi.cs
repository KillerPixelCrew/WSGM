using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Hybrid processor core placement, behind WSGM's policy test seam.</summary>
internal interface IHybridCoreApi
{
    Guid ReadActiveScheme();
    HybridCoreSupport Query(Guid scheme);
    HybridCoreState Read(Guid scheme, bool onBattery);
    void Write(Guid scheme, bool onBattery, HybridCoreState state);
    void RefreshActiveScheme();
}

/// <summary>Adapts the reusable library to WSGM's policy test seam.</summary>
internal sealed class WindowsHybridCoreApi : IHybridCoreApi
{
    public Guid ReadActiveScheme()
    {
        return WindowsPower.GetActiveScheme();
    }

    public HybridCoreSupport Query(Guid scheme)
    {
        return WindowsPower.QueryHybridCores(scheme);
    }

    public HybridCoreState Read(Guid scheme, bool onBattery)
    {
        return WindowsPower.ReadHybridCores(scheme, onBattery);
    }

    public void Write(Guid scheme, bool onBattery, HybridCoreState state)
    {
        WindowsPower.WriteHybridCores(scheme, onBattery, state);
    }

    public void RefreshActiveScheme()
    {
        WindowsPower.RefreshActiveScheme();
    }
}
