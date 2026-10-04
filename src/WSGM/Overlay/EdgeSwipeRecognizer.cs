using System;

namespace WSGM.Overlay;

/// <summary>
///     The pure edge-swipe decision: start bands, entry direction and trigger travel, in screen
///     pixels. It knows nothing about raw input, windows or the UI thread.
/// </summary>
internal static class EdgeSwipeRecognizer
{
    // Revised from the 2026-09-26 Claw traces, where 8 of 9 deliberate top swipes were rejected.
    // The start zone follows Windows' 2 mm first-report tolerance for an edge swipe; HHD's 2 %
    // start zone is the fallback when the digitizer reports no physical size. Direction is decided
    // once, from net displacement at the entry slop, the way Android's back gesture and GNOME's edge
    // drag do, so report-to-report jitter never accumulates against a straight swipe.

    /// <summary>The first-contact zone along an edge, in millimetres.</summary>
    internal const double StartBandMm = 2.0;

    /// <summary>The first-contact zone as a fraction of the axis when the digitizer reports no size.</summary>
    internal const double StartBandFraction = 0.02;

    /// <summary>The narrowest first-contact zone, in pixels.</summary>
    internal const int MinStartBandPx = 8;

    /// <summary>The widest first-contact zone, in pixels.</summary>
    internal const int MaxStartBandPx = 48;

    /// <summary>The net displacement that decides a contact's direction, in pixels.</summary>
    internal const int EntrySlopPx = 16;

    /// <summary>How long a contact may take to leave the entry slop.</summary>
    internal const ulong EntryWindowMs = 400;

    /// <summary>The inward travel that triggers a swipe, in pixels.</summary>
    internal const int TriggerDistancePx = 48;

    /// <summary>How long a contact may take to trigger.</summary>
    internal const ulong TriggerTimeMs = 800;

    /// <summary>
    ///     Converts a HID axis's physical extent to millimetres, or returns 0 when the descriptor
    ///     gives no plausible linear size for a handheld or tablet panel.
    /// </summary>
    /// <param name="units">The HID Unit item: nibble 0 is the system, nibble 1 the length exponent.</param>
    /// <param name="unitsExp">The HID Unit Exponent item, a four-bit signed code.</param>
    /// <param name="physicalMin">The axis's Physical Minimum.</param>
    /// <param name="physicalMax">The axis's Physical Maximum.</param>
    /// <returns>The axis length in millimetres, or 0 when unknown.</returns>
    internal static double PhysicalSpanMm(uint units, uint unitsExp, int physicalMin, int physicalMax)
    {
        // Only plain length is usable: SI linear (centimetres) or English linear (inches), to the
        // first power, with no mass, time or other unit nibbles set.
        var unitMm = units switch
        {
            0x11 => 10.0,
            0x13 => 25.4,
            _ => 0.0
        };
        if (unitMm == 0 || physicalMax <= physicalMin)
        {
            return 0;
        }

        // Codes 0x0-0x7 are exponents 0..7 and 0x8-0xF are -8..-1. Some descriptors store the
        // exponent as a signed byte (0xFE for -2); its low nibble carries the same code.
        var exponent = (int)(unitsExp & 0xF);
        if (exponent >= 8)
        {
            exponent -= 16;
        }

        var span = (physicalMax - physicalMin) * unitMm * Math.Pow(10, exponent);
        return span is >= 30 and <= 600 ? span : 0;
    }

    /// <summary>Returns the first-contact zone width, in pixels, along one screen axis.</summary>
    /// <param name="axisSpanMm">The digitizer's physical length on this axis, or 0 when unknown.</param>
    /// <param name="screenPx">The screen's pixel length on the same axis.</param>
    /// <returns>A zone of <see cref="StartBandMm" />, or <see cref="StartBandFraction" /> of the axis without a size.</returns>
    internal static int StartBandPx(double axisSpanMm, int screenPx)
    {
        var band = axisSpanMm > 0 ? StartBandMm * screenPx / axisSpanMm : StartBandFraction * screenPx;
        return Math.Clamp((int)Math.Round(band), MinStartBandPx, MaxStartBandPx);
    }

    /// <summary>Scales one raw digitizer coordinate to a screen pixel on the same axis.</summary>
    /// <param name="raw">The raw coordinate from the HID report.</param>
    /// <param name="logicalMin">The axis's Logical Minimum.</param>
    /// <param name="logicalMax">The axis's Logical Maximum, greater than the minimum.</param>
    /// <param name="screenPx">The screen's pixel length on the same axis.</param>
    /// <returns>The screen coordinate in physical pixels.</returns>
    internal static int ScaleToScreen(uint raw, int logicalMin, int logicalMax, int screenPx)
    {
        return (int)((raw - logicalMin) * (screenPx - 1) / (logicalMax - logicalMin));
    }

    /// <summary>Calculates how far a contact has moved inward from its tracked edge.</summary>
    /// <param name="edge">The edge that started the gesture.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <returns>The signed inward distance in physical pixels.</returns>
    internal static int InwardDistance(ScreenEdge edge, int startX, int startY, int x, int y)
    {
        return edge switch
        {
            ScreenEdge.Bottom => startY - y,
            ScreenEdge.Right => startX - x,
            ScreenEdge.Left => x - startX,
            ScreenEdge.Top => y - startY,
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    /// <summary>Calculates the net movement along a tracked edge, parallel to it.</summary>
    /// <param name="edge">The edge that started the gesture.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <returns>The absolute sideways displacement in physical pixels.</returns>
    internal static int SidewaysDistance(ScreenEdge edge, int startX, int startY, int x, int y)
    {
        return edge is ScreenEdge.Top or ScreenEdge.Bottom ? Math.Abs(x - startX) : Math.Abs(y - startY);
    }

    /// <summary>
    ///     Selects the candidate edge whose inward movement has crossed the trigger distance by the
    ///     greatest amount and dominates net sideways displacement 2:1. Tracking all candidates makes
    ///     corner-origin gestures follow their movement instead of an arbitrary edge priority.
    /// </summary>
    /// <param name="bottomCandidate">Whether the contact began inside the bottom band.</param>
    /// <param name="rightCandidate">Whether the contact began inside the right band.</param>
    /// <param name="leftCandidate">Whether the contact began inside the left band.</param>
    /// <param name="topCandidate">Whether the contact began inside the top band.</param>
    /// <param name="startX">Starting horizontal screen coordinate.</param>
    /// <param name="startY">Starting vertical screen coordinate.</param>
    /// <param name="x">Current horizontal screen coordinate.</param>
    /// <param name="y">Current vertical screen coordinate.</param>
    /// <param name="triggerDistance">Required inward distance in physical pixels.</param>
    /// <returns>The movement-matching edge, or null while none has crossed the threshold.</returns>
    internal static ScreenEdge? PickTriggeredEdge(
        bool bottomCandidate, bool rightCandidate, bool leftCandidate, bool topCandidate,
        int startX, int startY, int x, int y, int triggerDistance)
    {
        ScreenEdge? bestEdge = null;
        var bestDistance = triggerDistance - 1;
        Consider(ScreenEdge.Bottom, bottomCandidate);
        Consider(ScreenEdge.Right, rightCandidate);
        Consider(ScreenEdge.Left, leftCandidate);
        Consider(ScreenEdge.Top, topCandidate);
        return bestEdge;

        void Consider(ScreenEdge edge, bool candidate)
        {
            if (!candidate)
            {
                return;
            }

            var distance = InwardDistance(edge, startX, startY, x, y);
            var sideways = SidewaysDistance(edge, startX, startY, x, y);
            if (distance <= bestDistance || distance < sideways * 2)
            {
                return;
            }

            bestDistance = distance;
            bestEdge = edge;
        }
    }

    /// <summary>Allocation-free state for one contact, independent of raw-input and UI ownership.</summary>
    internal struct GestureTrace
    {
        private int _candidates;
        private int _entered;

        internal GestureTrace(int x, int y, int screenWidth, int screenHeight, int horizontalBand, int verticalBand,
            bool bottomEnabled, bool rightEnabled, bool leftEnabled, bool topEnabled)
        {
            this = default;
            StartX = LastX = x;
            StartY = LastY = y;
            if (x >= 0 && y >= 0 && x < screenWidth && y < screenHeight)
            {
                Add(ScreenEdge.Bottom, bottomEnabled && y >= screenHeight - verticalBand);
                Add(ScreenEdge.Right, rightEnabled && x >= screenWidth - horizontalBand);
                Add(ScreenEdge.Left, leftEnabled && x < horizontalBand);
                Add(ScreenEdge.Top, topEnabled && y < verticalBand);
            }

            Decision = HasCandidates ? "waiting" : "outside-start-band";
        }

        internal int StartX { get; }
        internal int StartY { get; }
        internal int LastX { get; private set; }
        internal int LastY { get; private set; }
        internal int HorizontalTravel { get; private set; }
        internal int VerticalTravel { get; private set; }
        internal ulong? FirstMovementMs { get; private set; }
        internal ulong? EntryMs { get; private set; }
        internal string Decision { get; private set; }
        internal readonly bool HasCandidates => _candidates != 0;

        private void Add(ScreenEdge edge, bool enabled)
        {
            if (enabled)
            {
                _candidates |= 1 << (int)edge;
            }
        }

        internal ScreenEdge? Move(int x, int y, ulong elapsedMs)
        {
            HorizontalTravel += Math.Abs(x - LastX);
            VerticalTravel += Math.Abs(y - LastY);
            LastX = x;
            LastY = y;
            if (FirstMovementMs is null && Math.Max(Math.Abs(x - StartX), Math.Abs(y - StartY)) >= 2)
            {
                FirstMovementMs = elapsedMs;
            }

            if (!HasCandidates)
            {
                return null;
            }

            if (elapsedMs > TriggerTimeMs)
            {
                _candidates = 0;
                Decision = "expired";
                return null;
            }

            CheckEntry(ScreenEdge.Bottom, elapsedMs);
            CheckEntry(ScreenEdge.Right, elapsedMs);
            CheckEntry(ScreenEdge.Left, elapsedMs);
            CheckEntry(ScreenEdge.Top, elapsedMs);
            var admitted = _candidates & _entered;
            var edge = PickTriggeredEdge(
                (admitted & (1 << (int)ScreenEdge.Bottom)) != 0,
                (admitted & (1 << (int)ScreenEdge.Right)) != 0,
                (admitted & (1 << (int)ScreenEdge.Left)) != 0,
                (admitted & (1 << (int)ScreenEdge.Top)) != 0,
                StartX, StartY, x, y, TriggerDistancePx);
            if (edge is not null)
            {
                Decision = "accepted";
                _candidates = 0;
            }

            return edge;
        }

        /// <summary>Ends the gesture without a trigger, recording why.</summary>
        /// <param name="reason">The decision the diagnostic trace reports.</param>
        internal void Cancel(string reason)
        {
            _candidates = 0;
            Decision = reason;
        }

        private void CheckEntry(ScreenEdge edge, ulong elapsedMs)
        {
            var mask = 1 << (int)edge;
            if ((_candidates & mask) == 0 || (_entered & mask) != 0)
            {
                return;
            }

            // Direction is judged once, when the net displacement first leaves the slop. Until then
            // a finger resting on the bezel, or drifting while its edge coordinate is clamped, has
            // decided nothing; afterwards only the 2:1 trigger test applies.
            var inward = InwardDistance(edge, StartX, StartY, LastX, LastY);
            var sideways = SidewaysDistance(edge, StartX, StartY, LastX, LastY);
            if (Math.Max(Math.Abs(inward), sideways) < EntrySlopPx && elapsedMs <= EntryWindowMs)
            {
                return;
            }

            if (elapsedMs > EntryWindowMs)
            {
                _candidates &= ~mask;
                Decision = "late-entry";
            }
            else if (inward > sideways)
            {
                _entered |= mask;
                EntryMs = elapsedMs;
            }
            else
            {
                _candidates &= ~mask;
                Decision = "sideways-travel";
            }
        }
    }
}
