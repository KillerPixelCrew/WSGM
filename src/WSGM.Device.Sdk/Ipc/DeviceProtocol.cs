namespace WSGM.Device.Sdk.Ipc;

/// <summary>The wire encoding used by the current exact Device SDK API.</summary>
public static class DeviceProtocol
{
    /// <summary>Exact version required on every frame and in every plugin manifest.</summary>
    public const ushort Version = DeviceApi.Version;
}
