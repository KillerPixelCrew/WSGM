using System;
using WindowsDeviceControl;

namespace WSGM.Interop;

/// <summary>Hybrid processor core placement, behind WSGM's policy test seam.</summary>
internal interface IHybridCoreApi
{
    /// <inheritdoc cref="WindowsPower.QueryHybridCores" />
    HybridCoreSupport Query(Guid scheme);
    /// <inheritdoc cref="WindowsPower.ReadHybridCores" />
    HybridCoreState Read(Guid scheme, bool onBattery);
    /// <inheritdoc cref="WindowsPower.WriteHybridCores" />
    void Write(Guid scheme, bool onBattery, HybridCoreState state);
}

/// <summary>Adapts the reusable library to WSGM's policy test seam.</summary>
internal sealed class WindowsHybridCoreApi : IHybridCoreApi
{
    /// <inheritdoc />
    public HybridCoreSupport Query(Guid scheme)
    {
        return WindowsPower.QueryHybridCores(scheme);
    }

    /// <inheritdoc />
    public HybridCoreState Read(Guid scheme, bool onBattery)
    {
        return WindowsPower.ReadHybridCores(scheme, onBattery);
    }

    /// <inheritdoc />
    public void Write(Guid scheme, bool onBattery, HybridCoreState state)
    {
        WindowsPower.WriteHybridCores(scheme, onBattery, state);
    }
}
