namespace WSGM.Shell;

/// <summary>An acknowledged duty write; refused and uncertain outcomes both stop automatic writes.</summary>
internal readonly record struct DeviceFanCurveWriteResult(bool Applied, string? Reason = null);
