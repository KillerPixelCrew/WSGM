using System.Globalization;
using WSGM.Core;
using WSGM.Tests.Builders;

namespace WSGM.Tests.Core;

public sealed class AutoTdpTraceCsvTests
{
    [Fact]
    public void ColumnNamesAreUnique()
    {
        Assert.Equal(
            AutoTdpTraceCsv.ColumnNames.Count,
            AutoTdpTraceCsv.ColumnNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AFormattedRowHasOneFieldPerColumnAndQuotesTextThatNeedsIt()
    {
        AutoTdpTraceRow row = new()
        {
            Event = AutoTdpTraceEvent.Tick,
            Row = 7,
            Detail = "held, then \"settled\"",
            Decision = new AutoTdpDecision(AutoTdpAction.Probe, 16, "probe-down"),
            PreviousWatts = 18
        };

        var fields = AutoTdpTraceReplay.ParseLine(AutoTdpTraceCsv.Format(row));

        Assert.Equal(AutoTdpTraceCsv.ColumnNames.Count, fields.Count);
        Assert.Equal("held, then \"settled\"", Field(fields, "detail"));
        Assert.Equal("probe", Field(fields, "action"));
        Assert.Equal("-2", Field(fields, "delta_w"));
        Assert.Equal("tick", Field(fields, "event"));
    }

    [Fact]
    public void UnavailableValuesAreEmptyRatherThanZero()
    {
        var fields = AutoTdpTraceReplay.ParseLine(AutoTdpTraceCsv.Format(new AutoTdpTraceRow
        {
            Event = AutoTdpTraceEvent.Tick
        }));

        Assert.Equal(string.Empty, Field(fields, "observed_w"));
        Assert.Equal(string.Empty, Field(fields, "ratio"));
        Assert.Equal(string.Empty, Field(fields, "rtss_window_ms"));
    }

    [Fact]
    public void FrametimesRoundTripExactlySoReplayJudgesTheSameRatio()
    {
        var fields = AutoTdpTraceReplay.ParseLine(AutoTdpTraceCsv.Format(new AutoTdpTraceRow
        {
            Event = AutoTdpTraceEvent.Tick,
            TargetFrametimeMs = 1000d / 60,
            Frametime = new RtssFrametimeSample(1, "game.exe", 1000d / 57, 57, 12, 1000, 2000, 17_544)
        }));

        Assert.Equal(1000d / 60, double.Parse(Field(fields, "target_frametime_ms"), CultureInfo.InvariantCulture));
        Assert.Equal(1000d / 57, double.Parse(Field(fields, "rtss_mean_frametime_ms"), CultureInfo.InvariantCulture));
        Assert.Equal("1000", Field(fields, "rtss_window_ms"));
    }

    private static string Field(IReadOnlyList<string> fields, string column)
    {
        return fields[AutoTdpTraceCsv.ColumnNames.ToList().IndexOf(column)];
    }
}
