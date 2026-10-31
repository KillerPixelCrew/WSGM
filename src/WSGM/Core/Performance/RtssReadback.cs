using System;

namespace WSGM.Core;

/// <summary>Result of querying the active global or application profile.</summary>
/// <param name="Values">Frame-limit profile value and the adapter's current WSGM overlay level.</param>
/// <param name="Timestamp">UTC time assigned by the adapter to this observation.</param>
/// <param name="OverlayPresentationEnabled">RTSS's presentation gates, independent of WSGM's selector; null when unknown or off.</param>
internal sealed record RtssReadback(PerformanceValues Values, DateTimeOffset Timestamp,
    bool? OverlayPresentationEnabled = null);
