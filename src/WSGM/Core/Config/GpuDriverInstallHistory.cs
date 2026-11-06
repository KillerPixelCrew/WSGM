namespace WSGM.Core;

/// <summary>Durable record written before dispatching a GPU installer, preserving an interrupted outcome.</summary>
public sealed record GpuDriverInstallHistory(string Adapter, string PreviousVersion, string NewVersion,
    bool Pending, string Result);
