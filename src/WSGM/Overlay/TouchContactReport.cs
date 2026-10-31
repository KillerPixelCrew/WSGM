namespace WSGM.Overlay;

/// <summary>One decoded touch report: the primary contact of a digitizer frame.</summary>
/// <param name="Digitizer">The digitizer that sent it, with its logical and physical ranges.</param>
/// <param name="Contacts">How many contacts the frame reports; at least 1.</param>
/// <param name="TipDown">Whether the primary contact touches the panel.</param>
/// <param name="RawX">The primary contact's raw X, or 0 when the tip is up.</param>
/// <param name="RawY">The primary contact's raw Y, or 0 when the tip is up.</param>
internal readonly record struct TouchContactReport(
    TouchDigitizer Digitizer,
    uint Contacts,
    bool TipDown,
    uint RawX,
    uint RawY);
