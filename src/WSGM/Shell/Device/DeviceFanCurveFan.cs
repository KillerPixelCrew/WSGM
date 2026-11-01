namespace WSGM.Shell;

/// <summary>A logical fan and the writable duty bounds its current descriptor declares.</summary>
internal readonly record struct DeviceFanCurveFan(string InstanceId, int MinimumDuty = 0, int MaximumDuty = 100);
