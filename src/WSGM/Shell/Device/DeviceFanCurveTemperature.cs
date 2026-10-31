using System;

namespace WSGM.Shell;

/// <summary>A temperature observation, including the time its source actually sampled it.</summary>
internal readonly record struct DeviceFanCurveTemperature(double? Celsius, DateTimeOffset CapturedAt);
