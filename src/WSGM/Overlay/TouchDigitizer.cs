namespace WSGM.Overlay;

/// <summary>A touch digitizer as raw input decodes it: its HID layout and its coordinate ranges.</summary>
internal sealed class TouchDigitizer
{
    public ushort ContactCountCollection;
    public bool HasContactCount;
    public ushort LinkCollection;
    public nint PreparsedData;
    public bool Usable;

    /// <summary>
    ///     Usage-list capacity for HidP_GetUsages, from HidP_GetCaps
    ///     (NumberInputDataIndices bounds the usages one input report can carry).
    /// </summary>
    public int UsageListLength = 16;

    public bool WarnedBadReport;
    public bool WarnedUsagesFailed;
    public int XMax;
    public int XMin;
    public double XSpanMm;
    public int YMax;
    public int YMin;
    public double YSpanMm;
}
